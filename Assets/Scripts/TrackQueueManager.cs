using System.Collections.Generic;
using UnityEngine;
using System.Collections;
using UnityEngine.UI;
using TMPro;
using System.IO;
using System.Linq;
using System;
using System.Threading.Tasks;
using MongoDBModels;
using System.Net.Sockets;
using System.Reflection;

public class TracklistLoadingResult
{
    public List<TracklistEntryDocument> ValidQueuedSongs { get; set; }
    public List<TracklistEntryDocument> ValidPlayingSongs { get; set; }
    public string ErrorMessage { get; set; }
    public bool IsComplete { get; set; }
}

public class TracklistMonitoringResult
{
    public List<TracklistEntryDocument> NewSongs { get; set; }
    public string ErrorMessage { get; set; }
    public bool IsComplete { get; set; }
}

public class TrackQueueManager : MonoBehaviour
{
    public Transform SongContainer;
    public Song SongPrefab;

    public TMP_Text timeText;
    public TMP_Text PlayedSongName;


    public int currentSongIndex;
    private bool isPaused = false;
    private bool wasPaused = false; // Track if song was paused vs finished

    private AudioSource audioSource;
    private AlbumManager albumManager;

    public List<(Song, GameObject)> queueList = new List<(Song, GameObject)>();
    private bool _isSlavePlaying = false;
    public bool isSlavePlaying 
    { 
        get => _isSlavePlaying; 
        set 
        { 
            Debug.Log($"[TRACKQUEUE] isSlavePlaying changed from {_isSlavePlaying} to {value} - Stack trace: {System.Environment.StackTrace}");
            _isSlavePlaying = value; 
        } 
    }
    public bool isFirstSong  = true;
    private MasterNetworkHandler masterNetworkHandler;
    private bool isPlaying = false;
    private Coroutine playbackCoroutine = null; // Store the coroutine for control
    private MongoDBIntegration mongoDBIntegration;
    private MongoDBManager mongoDBManager;
    private MongoDBSlaveController mongoDBSlaveController;
    private MongoDBMasterController mongoDBMasterController;
    private WebSocketSlaveClient webSocketClient;
    private Queue<TracklistUpdate> webSocketMessageQueue = new Queue<TracklistUpdate>();
    private int messageCounter = 0;
    private HashSet<string> processedMessages = new HashSet<string>();
    private Dictionary<string, float> lastSongAddedTime = new Dictionary<string, float>();
    private Coroutine tracklistPollingCoroutine;
    private TracklistEntryDocument currentPlayingTrack;
    
    // TCP Connection Monitoring (for slave mode)
    private SlaveController slaveController;
    private Coroutine tcpConnectionMonitorCoroutine;
    private bool wasTcpConnected = false;
    
    // Skip Cooldown Feature
    private float lastSkipTime = 0f;
    private float skipCooldownDuration = 1f; // Ignore insertions for 1 second after skip
    private bool isSkipping = false; // Prevent duplicate skip operations
    private void Start()
    {
        Debug.Log("[TRACKQUEUE] Starting TrackQueueManager...");
        
        albumManager = FindObjectOfType<AlbumManager>();
        if (albumManager == null)
        {
            Debug.LogError("[TRACKQUEUE] AlbumManager not found in the scene!");
            return;
        }

        mongoDBManager = MongoDBManager.Instance;
        if (mongoDBManager == null)
        {
            Debug.LogError("[TRACKQUEUE] MongoDBManager not found in the scene!");
            return;
        }
        
        Debug.Log("[TRACKQUEUE] TrackQueueManager initialized successfully");

        // Load existing tracklist entries on startup (non-blocking)
        StartCoroutine(LoadExistingTracklistOnStartupAsync());
        
        // Start continuous tracklist monitoring (disabled - using WebSocket only)
        // StartCoroutine(MonitorTracklistChanges());

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            Debug.LogError("No AudioSource component found on this GameObject!");
        }
        masterNetworkHandler = FindAnyObjectByType<MasterNetworkHandler>();
        mongoDBIntegration = FindObjectOfType<MongoDBIntegration>();
        mongoDBManager = MongoDBManager.Instance;
        mongoDBSlaveController = FindObjectOfType<MongoDBSlaveController>();
        mongoDBMasterController = FindObjectOfType<MongoDBMasterController>();
        
        // Initialize WebSocket client for real-time updates (slave mode only)
        if (albumManager.isSlave)
        {
            InitializeWebSocketClient();
            
            // Start monitoring TCP connection to clear queue on disconnection
            slaveController = FindObjectOfType<SlaveController>();
            if (slaveController != null)
            {
                tcpConnectionMonitorCoroutine = StartCoroutine(MonitorTcpConnection());
            }
            else
            {
                Debug.LogWarning("[TRACKQUEUE] SlaveController not found - TCP connection monitoring disabled");
            }
        }
        
        // Start polling for tracklist updates
        // DISABLED: This was causing double processing with MongoDBMasterController
        // StartTracklistPolling();
    }

    private void Update()
    {
        // Process WebSocket messages on main thread
        ProcessWebSocketMessageQueue();
        
        // Update UI based on mode
        if (albumManager.isSlave)
        {
            // Slave mode - update UI when playing
            if (isSlavePlaying && queueList.Count > 0)
            {
                UpdateSlaveUI();
            }
        }
        else
        {
            // Master mode - update UI when audio is playing
            if (audioSource.isPlaying)
            {
                UpdateUI();
            }
        }
    }
    
    private void ProcessWebSocketMessageQueue()
    {
        if (webSocketMessageQueue.Count > 0)
        {
            Debug.Log($"[TRACKQUEUE] Processing {webSocketMessageQueue.Count} queued WebSocket messages");
        }
        
        while (webSocketMessageQueue.Count > 0)
        {
            TracklistUpdate update = webSocketMessageQueue.Dequeue();
            Debug.Log($"[TRACKQUEUE] Dequeued message: {update.operationType} - {update.songTitle} - {update.status}");
            ProcessWebSocketUpdate(update);
        }
    }
    
    private void ProcessWebSocketUpdate(TracklistUpdate update)
    {
        // Create a unique message identifier to prevent duplicate processing
        string messageId = $"{update.operationType}_{update.songTitle}_{update.status}_{update.songId}";
        
        // For pause/resume commands, skip deduplication entirely to allow retries
        if (processedMessages.Contains(messageId))
        {
            // Check if this is a pause or resume command and skip deduplication
            if (update.operationType.ToLower() == "update" && 
                (update.status.ToLower() == "playing" || update.status.ToLower() == "paused"))
            {
                Debug.Log($"[TRACKQUEUE] Pause/Resume command - skipping deduplication to allow retry: {messageId}");
            }
            else
            {
                Debug.Log($"[TRACKQUEUE] Message already processed, skipping: {messageId}");
                return;
            }
        }
        
        Debug.Log($"[TRACKQUEUE] Processing message: {messageId}");
        Debug.Log($"[TRACKQUEUE] Message details - operationType: {update.operationType}, status: {update.status}, songTitle: {update.songTitle}");
        
        // Mark this message as processed (only if not a pause/resume command)
        if (!(update.operationType.ToLower() == "update" && 
              (update.status.ToLower() == "playing" || update.status.ToLower() == "paused")))
        {
            processedMessages.Add(messageId);
        }
        
        // Clean up old processed messages (keep only last 100 to prevent memory growth)
        if (processedMessages.Count > 100)
        {
            var oldMessages = processedMessages.Take(50).ToList();
            foreach (var oldMessage in oldMessages)
            {
                processedMessages.Remove(oldMessage);
            }
        }
        
        // Clean up old song time tracking (remove entries older than 10 seconds)
        CleanupOldSongTimes();
        
        Debug.Log($"[TRACKQUEUE] Processing WebSocket update on main thread: {update.operationType} - {update.songTitle} - Status: {update.status}");
        Debug.Log($"[TRACKQUEUE] Current queue size before processing: {queueList.Count}");
        
        // Handle "update" operations based on status
        if (update.operationType.ToLower() == "update")
        {
            Debug.Log($"[TRACKQUEUE] Processing update operation with status: {update.status}");
            switch (update.status.ToLower())
            {
                case "paused":
                    Debug.Log($"[TRACKQUEUE] Calling HandleWebSocketPause()");
                    HandleWebSocketPause();
                    break;
                    
                case "playing":
                    // Check if this is a skip operation (song already exists in queue) or new song
                    bool songExistsInQueue = IsSongAlreadyInQueue(update.songTitle, update.songId);
                    
                    if (songExistsInQueue)
                    {
                        // This is a skip operation - just update the current song, don't add new one
                        Debug.Log($"[TRACKQUEUE] Song already exists - treating as skip operation: {update.songTitle}");
                        HandleWebSocketSkipToSong(update);
                    }
                    else if (update.songId != null && !string.IsNullOrEmpty(update.songId))
                    {
                        // This is a new song with complete data - treat as insert
                        Debug.Log($"[TRACKQUEUE] New song with playing status - calling HandleWebSocketInsert()");
                        HandleWebSocketInsert(update);
                    }
                    else
                    {
                        // This is a resume command for existing song
                        Debug.Log($"[TRACKQUEUE] Resume command - calling HandleWebSocketResume()");
                        HandleWebSocketResume();
                    }
                    break;
                    
                case "queued":
                    Debug.Log($"[TRACKQUEUE] Calling HandleWebSocketInsert()");
                    HandleWebSocketInsert(update);
                    break;
                    
                case "skipped":
                    Debug.Log($"[TRACKQUEUE] Calling HandleWebSocketSkip()");
                    HandleWebSocketSkip(update.songIndex);
                    break;
                    
                default:
                    Debug.LogWarning($"[TRACKQUEUE] Unknown update status: {update.status}");
                    break;
            }
        }
        else
        {
            // Handle direct operation types
            switch (update.operationType.ToLower())
            {
                case "pause":
                    HandleWebSocketPause();
                    break;
                    
                case "resume":
                    HandleWebSocketResume();
                    break;
                    
                case "skip":
                    HandleWebSocketSkip(update.songIndex);
                    break;
                    
                case "insert":
                    HandleWebSocketInsert(update);
                    break;
                    
                case "delete":
                    HandleWebSocketDelete(update);
                    break;
                    
                default:
                    Debug.LogWarning($"[TRACKQUEUE] Unknown WebSocket operation: {update.operationType}");
                    break;
            }
        }
        
        Debug.Log($"[TRACKQUEUE] Current queue size after processing: {queueList.Count}");
    }

    public float slaveCurrentTime = 0f;

    private void StartTracklistPolling()
    {
        if (tracklistPollingCoroutine != null)
        {
            StopCoroutine(tracklistPollingCoroutine);
        }
        tracklistPollingCoroutine = StartCoroutine(PollTracklistUpdates());
    }

    private IEnumerator PollTracklistUpdates()
    {
        // DISABLED: This function is no longer used - all tracklist updates come through WebSocket
        Debug.Log("[TRACKQUEUE] PollTracklistUpdates is disabled - using WebSocket only");
        yield break;
        
        while (true)
        {
            yield return new WaitForSeconds(2f); // Poll every 2 seconds
            
            if (mongoDBManager != null)
            {
                _ = CheckForTracklistUpdates();
            }
        }
    }

    private async Task CheckForTracklistUpdates()
    {
        try
        {
            // Check if there's a song that should be playing but isn't
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();

            // If no song is playing but there are queued songs, start the next one
            if (playingSongs.Count == 0 && queuedSongs.Count > 0 && !isPlaying)
            {
                await PlayNextSongFromTracklist();
            }
            // If a song is marked as playing but we're not playing anything, sync up
            else if (playingSongs.Count > 0 && !audioSource.isPlaying && !isPaused)
            {
                var trackToPlay = playingSongs[0];
                await LoadAndPlayTrack(trackToPlay);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error checking tracklist updates: {ex.Message}");
        }
    }

    private async Task PlayNextSongFromTracklist()
    {
        try
        {
            var nextTrack = await mongoDBManager.GetNextSongAsync();
            if (nextTrack != null)
            {
                await LoadAndPlayTrack(nextTrack);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error playing next song from tracklist: {ex.Message}");
        }
    }

    private async Task LoadAndPlayTrack(TracklistEntryDocument track)
    {
        try
        {
            currentPlayingTrack = track;
            
            // Create UI representation
            if (SongPrefab != null && SongContainer != null)
            {
                Song songInstance = Instantiate(SongPrefab, SongContainer);
                songInstance.Initialize(track.Title, track.Artist, "", track.Id);
                queueList.Add((songInstance, songInstance.gameObject));
            }

            // Update UI
            PlayedSongName.text = track.Title;
            
            // Start playback
            if (!albumManager.isSlave)
            {
                audioSource.volume = 1f;
                // Note: In a real implementation, you'd load the actual audio file here
                // For now, we'll simulate playback duration
                int duration = track.Duration ?? 180; // Default to 180 seconds if null
                StartCoroutine(SimulatePlayback(duration));
            }
            else
            {
                // Slave mode - just track time
                slaveCurrentTime = 0f;
                isSlavePlaying = true;
                StartCoroutine(SimulateSlavePlayback(track.Duration));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading and playing track: {ex.Message}");
        }
    }

    private IEnumerator SimulatePlayback(int duration)
    {
        isPlaying = true;
        float elapsed = 0f;

        while (elapsed < duration && isPlaying)
        {
            if (!isPaused)
            {
                elapsed += Time.deltaTime;
                UpdateUI();
            }
            yield return null;
        }

        // Song finished — call async method safely (do not await in coroutine)
        FireAndForget(MarkCurrentSongAsPlayed());
        isPlaying = false;
    }

    public IEnumerator SimulateSlavePlayback(int? duration)
    {
        yield return StartCoroutine(SimulateSlavePlaybackFromTime(duration, 0f));
    }
    
    public IEnumerator SimulateSlavePlaybackFromTime(int? duration, float startTime)
    {
        float actualDuration = duration ?? 180f; // Use duration from MongoDB or default to 180 seconds
        float elapsed = startTime; // Start from the specified time
        
        Debug.Log($"[TRACKQUEUE] Starting SimulateSlavePlaybackFromTime - duration: {actualDuration}, startTime: {startTime}, isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}");
        
        while (elapsed < actualDuration && isSlavePlaying)
        {
            if (!isPaused)
            {
                elapsed += Time.deltaTime;
                slaveCurrentTime = elapsed;
                UpdateSlaveUI();
                
                // Debug every 5 seconds
                if (Mathf.FloorToInt(elapsed) % 5 == 0 && Mathf.FloorToInt(elapsed) != Mathf.FloorToInt(elapsed - Time.deltaTime))
                {
                    Debug.Log($"[TRACKQUEUE] SimulateSlavePlayback - elapsed: {elapsed:F1}s, isPaused: {isPaused}, isSlavePlaying: {isSlavePlaying}");
                }
            }
            else
            {
                // Debug when paused
                if (Mathf.FloorToInt(Time.time) % 2 == 0 && Mathf.FloorToInt(Time.time) != Mathf.FloorToInt(Time.time - Time.deltaTime))
                {
                    Debug.Log($"[TRACKQUEUE] SimulateSlavePlayback PAUSED - elapsed: {elapsed:F1}s, isPaused: {isPaused}, isSlavePlaying: {isSlavePlaying}");
                }
            }
            yield return null;
        }
        
        Debug.Log($"[TRACKQUEUE] SimulateSlavePlayback finished - elapsed: {elapsed:F1}s, isSlavePlaying: {isSlavePlaying}, reason: {(elapsed >= actualDuration ? "duration reached" : "isSlavePlaying = false")}");
        
        // Song finished — call async method safely (do not await in coroutine)
        FireAndForget(MarkCurrentSongAsPlayed());
        isSlavePlaying = false;
        wasPaused = false; // Reset pause flag when song finishes naturally
    }

    private async Task MarkCurrentSongAsPlayed()
    {
        if (currentPlayingTrack != null)
        {
            await mongoDBManager.MarkSongAsPlayedAsync(currentPlayingTrack.Id);
            currentPlayingTrack = null;
        }
    }

    private void UpdateUI()
    {
        if (audioSource.clip != null)
        {
            float currentTime = audioSource.time;
            float totalTime = audioSource.clip.length;
            timeText.text = FormatTime(currentTime) + "/" + FormatTime(totalTime);
            albumManager.UpdateDebugText("clip not null");
        }
     
    }

    private IEnumerator LoadExistingTracklistOnStartupAsync()
    {
        Debug.Log("[TRACKQUEUE] Loading existing tracklist entries on startup...");
        albumManager.UpdateDebugText("Loading existing tracklist...");

        // Wait a moment for MongoDB to be fully initialized
        yield return new WaitForSeconds(2f);

        List<TracklistEntryDocument> validQueuedSongs = null;
        List<TracklistEntryDocument> validPlayingSongs = null;
        string errorMessage = null;

        // Use WaitUntil to wait for async operations without blocking
        var loadingResult = new TracklistLoadingResult();
        
        // Start async loading in background
        _ = LoadTracklistDataAsync(loadingResult);
        
        // Wait until loading is complete
        yield return new WaitUntil(() => loadingResult.IsComplete);
        
        // Get results from the wrapper
        validQueuedSongs = loadingResult.ValidQueuedSongs;
        validPlayingSongs = loadingResult.ValidPlayingSongs;
        errorMessage = loadingResult.ErrorMessage;

        if (errorMessage != null)
        {
            yield break;
        }

        // Process playing songs first (they should start playing immediately)
        foreach (var track in validPlayingSongs)
        {
            Debug.Log($"[TRACKQUEUE] Loading playing song: {track.Title}");
            yield return StartCoroutine(LoadTrackFromMongoDB(track, true));
        }

        // Process queued songs (add to queue but don't start playing yet)
        foreach (var track in validQueuedSongs)
        {
            Debug.Log($"[TRACKQUEUE] Loading queued song: {track.Title}");
            yield return StartCoroutine(LoadTrackFromMongoDB(track, false));
        }

        // If we loaded any playing songs, start playback
        if (validPlayingSongs.Count > 0 && !albumManager.isSlave)
        {
            Debug.Log("[TRACKQUEUE] Starting playback of loaded playing songs");
            PlayQueue();
        }

        albumManager.UpdateDebugText($"Loaded {validQueuedSongs.Count + validPlayingSongs.Count} songs from tracklist");
    }

    private async Task LoadTracklistDataAsync(TracklistLoadingResult result)
    {
        try
        {
            // Get all queued and playing songs that exist at master
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            
            // Filter only songs that exist at master
            result.ValidQueuedSongs = queuedSongs.Where(s => s.ExistsAtMaster).ToList();
            result.ValidPlayingSongs = playingSongs.Where(s => s.ExistsAtMaster).ToList();

            Debug.Log($"[TRACKQUEUE] Found {result.ValidQueuedSongs.Count} valid queued songs and {result.ValidPlayingSongs.Count} valid playing songs");
            result.IsComplete = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TRACKQUEUE] Error loading existing tracklist: {ex.Message}");
            albumManager.UpdateDebugText($"Error loading tracklist: {ex.Message}");
            result.ErrorMessage = ex.Message;
            result.IsComplete = true;
        }
    }

    private async Task LoadMonitoringDataAsync(TracklistMonitoringResult result)
    {
        try
        {
            // Get all queued songs that exist at master
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();
            var validSongs = queuedSongs.Where(s => s.ExistsAtMaster).ToList();
            
            // Filter out songs already in Unity queue
            result.NewSongs = validSongs.Where(song => !IsSongAlreadyInUnityQueue(song)).ToList();
            result.IsComplete = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TRACKQUEUE] Error monitoring tracklist changes: {ex.Message}");
            result.ErrorMessage = ex.Message;
            result.IsComplete = true;
        }
    }

    private IEnumerator LoadTrackFromMongoDB(TracklistEntryDocument track, bool shouldPlay)
    {
        // Find the album folder for this track
        string albumPath = albumManager.FindAlbumFolder(track.Album);
        if (string.IsNullOrEmpty(albumPath))
        {
            Debug.LogWarning($"[TRACKQUEUE] Album folder not found for: {track.Album}");
            yield break;
        }

        // Find the audio file for this track
        string audioPath = albumManager.FindSongFilePath(albumPath, track.Title);
        if (string.IsNullOrEmpty(audioPath))
        {
            Debug.LogWarning($"[TRACKQUEUE] Audio file not found for: {track.Title}");
            yield break;
        }

        Debug.Log($"[TRACKQUEUE] Found valid path for {track.Title}: {audioPath}");

        // Add to Unity queue
        float duration = track.Duration ?? 180f; // Default to 180 seconds if null
        yield return StartCoroutine(AddSongToQueueWithPath(track.Title, audioPath, duration, false));

        // If this was a playing song, mark it as playing in MongoDB
        if (shouldPlay && mongoDBMasterController != null)
        {
            try
            {
                _ = NotifyMongoDBSongPlaying(track.Title);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TRACKQUEUE] Error notifying MongoDB of playing song {track.Title}: {ex.Message}");
            }
        }
    }

    private IEnumerator MonitorTracklistChanges()
    {
        // DISABLED: This function is no longer used - all tracklist updates come through WebSocket
        Debug.Log("[TRACKQUEUE] MonitorTracklistChanges is disabled - using WebSocket only");
        yield break;
        
        Debug.Log("[TRACKQUEUE] Starting continuous tracklist monitoring...");
        
        // Wait for initial loading to complete
        yield return new WaitForSeconds(5f);
        
        while (true)
        {
            // Poll for new tracklist entries every 3 seconds
            yield return new WaitForSeconds(3f);
            
            Debug.Log("[TRACKQUEUE] Checking for new tracklist entries...");
            
            List<TracklistEntryDocument> newSongs = null;
            string errorMessage = null;
            
            // Use async loading without blocking
            var monitoringResult = new TracklistMonitoringResult();
            _ = LoadMonitoringDataAsync(monitoringResult);
            
            // Wait for async operation to complete
            yield return new WaitUntil(() => monitoringResult.IsComplete);
            
            // Get results from the wrapper
            newSongs = monitoringResult.NewSongs;
            errorMessage = monitoringResult.ErrorMessage;
            
            if (errorMessage != null)
            {
                continue; // Skip this iteration and try again next time
            }
            
            if (newSongs.Count > 0)
            {
                Debug.Log($"[TRACKQUEUE] Found {newSongs.Count} new songs to add to queue");
                
                foreach (var track in newSongs)
                {
                    Debug.Log($"[TRACKQUEUE] Adding new song from tracklist: {track.Title}");
                    yield return StartCoroutine(LoadTrackFromMongoDB(track, false));
                }
            }
            else
            {
                Debug.Log("[TRACKQUEUE] No new songs found in tracklist");
            }
        }
    }

    private bool IsSongAlreadyInUnityQueue(TracklistEntryDocument track)
    {
        return queueList.Any(q => q.Item1.SongName == track.Title);
    }

    public IEnumerator AddSongToQueueByName(string songFileName, float length = 0f, bool isFromSlave = false)
    {
        Debug.Log($"[TRACKQUEUE] AddSongToQueueByName called - Song: {songFileName}, Length: {length}, FromSlave: {isFromSlave}");
        albumManager.UpdateDebugText($"Trying to add song: {songFileName}");

        // Use lazy path resolution - search for the song in album folders
        string fullPath = null;
        
        // First try to find in album folders using AlbumManager
        if (albumManager != null && !string.IsNullOrEmpty(albumManager.AlbumBasePath))
        {
            Debug.Log($"[TRACKQUEUE] Searching for song '{songFileName}' in album folders...");
            var albumFolders = Directory.GetDirectories(albumManager.AlbumBasePath);
            
            foreach (var albumFolder in albumFolders)
            {
                string foundPath = albumManager.FindSongFilePath(albumFolder, songFileName);
                if (!string.IsNullOrEmpty(foundPath))
                {
                    fullPath = foundPath;
                    Debug.Log($"[TRACKQUEUE] Found song in album folder: {fullPath}");
                    break;
                }
            }
        }
        
        // Fallback to old method if not found in album folders
        if (string.IsNullOrEmpty(fullPath))
        {
            Debug.Log($"[TRACKQUEUE] Song not found in album folders, trying old method...");
            string folderPath = PlayerPrefs.GetString("FriendlyAlbumsPath", "");

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                Debug.LogError($"[TRACKQUEUE] FriendlyAlbumsPath is invalid or does not exist: {folderPath}");
                albumManager.UpdateDebugText("FriendlyAlbumsPath is invalid or does not exist.");
                yield break;
            }

            string[] supportedExtensions = { ".mp3", ".wav", ".ogg" };

            // First try to find in root folder (for backward compatibility)
            fullPath = Directory.GetFiles(folderPath)
                               .FirstOrDefault(f =>
                                   Path.GetFileName(f).Equals(songFileName, StringComparison.OrdinalIgnoreCase) &&
                                   supportedExtensions.Contains(Path.GetExtension(f).ToLower()));

            // If not found in root, search in all subfolders (album folders)
            if (string.IsNullOrEmpty(fullPath))
            {
                Debug.Log($"[TRACKQUEUE] Song '{songFileName}' not found in root folder, searching subfolders...");
                var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
                fullPath = allFiles.FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(songFileName, StringComparison.OrdinalIgnoreCase) &&
                    supportedExtensions.Contains(Path.GetExtension(f).ToLower()));
            }
        }

        if (string.IsNullOrEmpty(fullPath))
        {
            Debug.LogError($"[TRACKQUEUE] Song '{songFileName}' not found in any location");
            albumManager.UpdateDebugText($"Song '{songFileName}' not found in any location.");
            yield break;
        }

        Debug.Log($"[TRACKQUEUE] Found song file: {fullPath}");

        if (SongPrefab == null || SongContainer == null)
        {
            Debug.LogError($"[TRACKQUEUE] SongPrefab or SongContainer is null! SongPrefab: {SongPrefab}, SongContainer: {SongContainer}");
            albumManager.UpdateDebugText("SongPrefab or SongContainer is null!");
            yield break;
        }

        string songName = Path.GetFileNameWithoutExtension(fullPath);
        Debug.Log($"[TRACKQUEUE] Creating song instance: {songName}");

        Song songInstance = Instantiate(SongPrefab, SongContainer);
        songInstance.Initialize(songName, "Unknown Artist", fullPath, songName); // Use songName as identifier
        queueList.Add((songInstance, songInstance.gameObject));
        
        Debug.Log($"[TRACKQUEUE] Added song to queue list: {songName} (GameObject: {songInstance.gameObject.name})");
        Debug.Log($"[TRACKQUEUE] Total songs in queue: {queueList.Count}");
        
        // Debug: List all songs in queue after adding
        Debug.Log($"[TRACKQUEUE] Current queue contents:");
        for (int i = 0; i < queueList.Count; i++)
        {
            Debug.Log($"[TRACKQUEUE]   [{i}] {queueList[i].Item1.SongName}");
        }

        if (isFromSlave)
        {
            songInstance.SongLength = length;
            albumManager.UpdateDebugText($"Slave added song with length: {songInstance.SongLength}.");
        }
        else
        {
            albumManager.UpdateDebugText($"Master loading song to get length: {songInstance.SongName}");

            yield return songInstance.StartCoroutine(songInstance.LoadAudioClipFromPath());

            AudioClip clip = songInstance.GetAudioClip();
            if (clip == null)
            {
                albumManager.UpdateDebugText("Error: AudioClip is NULL. Skipping song.");
                Debug.Log($"[TRACKQUEUE] Removing song due to NULL AudioClip: {songInstance.SongName}");
                queueList.Remove((songInstance, songInstance.gameObject));
                Debug.Log($"[TRACKQUEUE] Queue count after removal: {queueList.Count}");
                yield break;
            }

            songInstance.SongLength = clip.length;
            masterNetworkHandler?.SendSongWithLengthToSlave(songFileName, songInstance.SongLength);
        }

        if (!isPlaying && !albumManager.isSlave)
        {
            Debug.Log($"[TRACKQUEUE] Starting playback - Queue count: {queueList.Count}, isPlaying: {isPlaying}, isSlave: {albumManager.isSlave}");
            PlayQueue();
        }
        else
        {
            Debug.Log($"[TRACKQUEUE] Not starting playback - Queue count: {queueList.Count}, isPlaying: {isPlaying}, isSlave: {albumManager.isSlave}");
        }
    }

    private void UpdateSlaveUI()
    {

        // Safety check for currentSongIndex and queue
        if (currentSongIndex < 0 || currentSongIndex >= queueList.Count || queueList.Count == 0)
        {
            Debug.Log($"[TRACKQUEUE] UpdateSlaveUI - Safety check failed, stopping playback");
            // If queue is empty, stop playback
            if (queueList.Count == 0)
            {
                isSlavePlaying = false;
                isPaused = false;
                wasPaused = false; // Reset pause flag
                slaveCurrentTime = 0;
                currentSongIndex = 0;
            }
            return;
        }

        float totalTime = queueList[currentSongIndex].Item1.SongLength;
        
        // Check if song has finished
        if (slaveCurrentTime >= totalTime)
        {
            slaveCurrentTime = totalTime;
            SkipSongSlave(); // Automatically skip to the next song when it finishes
            return; // Exit the function to prevent further updates on the finished song
        }

        // Update the time display
        string timeDisplay = FormatTime(slaveCurrentTime) + "/" + FormatTime(totalTime);
        
        if (timeText != null)
        {
            timeText.text = timeDisplay;
        }
        else
        {
            Debug.LogError($"[TRACKQUEUE] timeText is null! Cannot update UI display.");
        }
    }

    private string FormatTime(float timeInSeconds)
    {
        int minutes = Mathf.FloorToInt(timeInSeconds / 60);
        int seconds = Mathf.FloorToInt(timeInSeconds % 60);
        return string.Format("{0:00}:{1:00}", minutes, seconds);
    }
    public async Task AddSongToQueue(string keypadInput, string requestedBy = "user")
    {
        try
        {
            Debug.Log($"[TRACKQUEUE] AddSongToQueue called - Input: {keypadInput}, RequestedBy: {requestedBy}");
            Debug.Log($"[TRACKQUEUE] AlbumManager.isSlave: {albumManager.isSlave}");
            albumManager.UpdateDebugText($"Adding song to queue: {keypadInput}");

            if (keypadInput.Length != 5 || keypadInput[2] != '-')
            {
                Debug.LogWarning($"[TRACKQUEUE] Invalid input format: {keypadInput}. Expected: DD-DD");
                albumManager.UpdateDebugText("Invalid input format. Expected format: DD-DD");
                return;
            }

            if (!int.TryParse(keypadInput.Substring(0, 2), out int albumIndex) ||
                !int.TryParse(keypadInput.Substring(3, 2), out int songIndex))
            {
                albumManager.UpdateDebugText($"Failed to parse album or song index from input: {keypadInput}");
                return;
            }

            if (albumIndex < 0 || albumIndex >= albumManager.albums.Count)
            {
                albumManager.UpdateDebugText($"Album index {albumIndex} is out of range.");
                return;
            }

            Album album = albumManager.albums[albumIndex - 1];

            if (songIndex <= 0 || songIndex > album.Songs.Count)
            {
                albumManager.UpdateDebugText($"Song index {songIndex} is out of range for album: {album.albumName}");
                return;
            }

            Song selectedSong = album.Songs[songIndex - 1];
            Debug.Log($"[TRACKQUEUE] STEP 1: Validating audio file exists for: {selectedSong.SongName}");

            // Check cooldown only for user-initiated actions (not for automated sources)
            if (requestedBy == "user" && IsSongInCooldown(selectedSong.SongName))
            {
                float timeSinceLastAdded = Time.time - lastSongAddedTime[selectedSong.SongName];
                Debug.Log($"[TRACKQUEUE] Song '{selectedSong.SongName}' is in cooldown period ({timeSinceLastAdded:F1}s / 5.0s), blocking user action");
                albumManager.UpdateDebugText($"Song '{selectedSong.SongName}' was recently added. Please wait {5.0f - timeSinceLastAdded:F1} seconds.");
                return;
            }

            // Find the corresponding MongoDB song
            var mongoSongs = await mongoDBManager.GetAllSongsAsync();
            var mongoSong = mongoSongs.Find(s => s.Title.Contains(selectedSong.SongName) && s.Album == album.albumName);

            if (mongoSong == null)
            {
                albumManager.UpdateDebugText($"Song not found in MongoDB: {selectedSong.SongName}");
                return;
            }

            // STEP 2: Add to MongoDB tracklist
            string masterId = albumManager.isSlave ? "slave" : "master";
            
            // Determine status based on whether this is the first song
            string status = "queued";
            if (albumManager.isSlave)
            {
                // For slave, check if this will be the first song in the tracklist
                var existingTracklist = await mongoDBManager.GetAllTracklistEntriesAsync();
                bool isFirstSong = existingTracklist.Count == 0;
                status = isFirstSong ? "playing" : "queued";
                Debug.Log($"[TRACKQUEUE] Slave adding song - isFirstSong: {isFirstSong}, status: {status}");
            }
            
            Debug.Log($"[TRACKQUEUE] Adding to MongoDB tracklist - SongId: {mongoSong.Id}, Title: {selectedSong.SongName}, MasterId: {masterId}, Status: {status}");
            
            var tracklistEntry = await mongoDBManager.AddSongToTracklistAsync(
                mongoSong.Id,
                selectedSong.SongName,
                selectedSong.Artist,
                album.albumName,
                0, // Duration will be set by master after validation
                requestedBy,
                masterId,
                1, // Default priority
                status
            );

            if (tracklistEntry != null)
            {
                Debug.Log($"[TRACKQUEUE] Successfully added to MongoDB tracklist - ID: {tracklistEntry.Id}, ExistsAtMaster: {tracklistEntry.ExistsAtMaster}");
                albumManager.UpdateDebugText($"Added {selectedSong.SongName} to tracklist - waiting for master validation");
                
                // Record cooldown time only for user-initiated actions (not for automated sources)
                if (requestedBy == "user")
                {
                    lastSongAddedTime[selectedSong.SongName] = Time.time;
                    Debug.Log($"[TRACKQUEUE] Recorded cooldown time for user-initiated song: {selectedSong.SongName}");
                }
            }
            else
            {
                Debug.LogError($"[TRACKQUEUE] Failed to add to MongoDB tracklist for song: {selectedSong.SongName}");
                albumManager.UpdateDebugText($"Failed to add {selectedSong.SongName} to tracklist");
                return;
            }
            
            // STEP 3: Only add to Unity queue if this is the master (immediate validation)
            if (!albumManager.isSlave)
            {
                Debug.Log($"[TRACKQUEUE] STEP 3: Master adding to Unity queue immediately after validation");
                
                // Find audio path for the song
                string albumPath = albumManager.FindAlbumFolder(album.albumName);
                string audioPath = albumManager.FindSongFilePath(albumPath, selectedSong.SongName);
                
                if (string.IsNullOrEmpty(audioPath))
                {
                    Debug.LogError($"[TRACKQUEUE] Audio file not found for song: {selectedSong.SongName}");
                    albumManager.UpdateDebugText($"Audio file not found for song: {selectedSong.SongName}");
                    return;
                }
                
                // Add to Unity queue using the found audio path
                StartCoroutine(AddSongToQueueWithPath(selectedSong.SongName, audioPath, selectedSong.SongLength, false));
                
                Debug.Log($"[TRACKQUEUE] Master added song to Unity queue: {selectedSong.SongName}");
            }
            else
            {
                Debug.Log($"[TRACKQUEUE] Slave added song to MongoDB tracklist - master will validate and send WebSocket update");
                albumManager.UpdateDebugText($"Added {selectedSong.SongName} to tracklist - waiting for master");
            }
        }
        catch (Exception ex)
        {
            albumManager.UpdateDebugText($"Error adding song to queue: {ex.Message}");
            Debug.LogError($"Error adding song to queue: {ex.Message}");
        }
    }

    // New method: Add song to Unity queue WITHOUT adding to MongoDB tracklist
    // This is used when processing songs from MongoDB tracklist to avoid infinite loops
    public async Task AddSongToUnityQueueFromMongoDB(string keypadInput, string requestedBy = "user")
    {
        try
        {
            Debug.Log($"[TRACKQUEUE] AddSongToUnityQueueFromMongoDB called - Input: {keypadInput}, RequestedBy: {requestedBy}");
            albumManager.UpdateDebugText($"Adding song to Unity queue from MongoDB: {keypadInput}");

            if (keypadInput.Length != 5 || keypadInput[2] != '-')
            {
                Debug.LogWarning($"[TRACKQUEUE] Invalid input format: {keypadInput}. Expected: DD-DD");
                albumManager.UpdateDebugText("Invalid input format. Expected format: DD-DD");
                return;
            }

            if (!int.TryParse(keypadInput.Substring(0, 2), out int albumIndex) ||
                !int.TryParse(keypadInput.Substring(3, 2), out int songIndex))
            {
                Debug.LogError($"[TRACKQUEUE] Failed to parse album/song index from: {keypadInput}");
                albumManager.UpdateDebugText($"Failed to parse album or song index from input: {keypadInput}");
                return;
            }

            Debug.Log($"[TRACKQUEUE] Parsed - Album: {albumIndex}, Song: {songIndex}");

            if (albumIndex < 0 || albumIndex >= albumManager.albums.Count)
            {
                Debug.LogError($"[TRACKQUEUE] Album index {albumIndex} out of range. Total albums: {albumManager.albums.Count}");
                albumManager.UpdateDebugText($"Album index {albumIndex} is out of range.");
                return;
            }

            Album album = albumManager.albums[albumIndex - 1];
            Debug.Log($"[TRACKQUEUE] Selected album: {album.albumName}");

            if (songIndex <= 0 || songIndex > album.Songs.Count)
            {
                Debug.LogError($"[TRACKQUEUE] Song index {songIndex} out of range for album {album.albumName}. Total songs: {album.Songs.Count}");
                albumManager.UpdateDebugText($"Song index {songIndex} is out of range for album: {album.albumName}");
                return;
            }

            Song selectedSong = album.Songs[songIndex - 1];
            Debug.Log($"[TRACKQUEUE] Selected song: {selectedSong.SongName}");
            Debug.Log($"[TRACKQUEUE] Song audio path: {selectedSong.AudioClipPath}");

            // If no audio path, search for it now
            string audioPath = selectedSong.AudioClipPath;
            if (string.IsNullOrEmpty(audioPath))
            {
                Debug.Log($"[TRACKQUEUE] No audio path found, searching for song: {selectedSong.SongName}");
                albumManager.UpdateDebugText($"Searching for audio file: {selectedSong.SongName}");
                
                // Find the album folder and audio file
                string albumPath = albumManager.FindAlbumFolder(album.albumName);
                if (string.IsNullOrEmpty(albumPath))
                {
                    Debug.LogError($"[TRACKQUEUE] Album folder not found for: {album.albumName}");
                    albumManager.UpdateDebugText($"Album folder not found for: {album.albumName}");
                    return;
                }
                
                audioPath = albumManager.FindSongFilePath(albumPath, selectedSong.SongName);
                if (string.IsNullOrEmpty(audioPath))
                {
                    Debug.LogError($"[TRACKQUEUE] Audio file not found for song: {selectedSong.SongName}");
                    albumManager.UpdateDebugText($"Audio file not found for song: {selectedSong.SongName}");
                    return;
                }
                
                Debug.Log($"[TRACKQUEUE] Found audio path: {audioPath}");
            }

            // Only add to Unity queue if this is the master (immediate validation)
            if (!albumManager.isSlave)
            {
                // Add directly to Unity queue using the audio path
                Debug.Log($"[TRACKQUEUE] Master adding song to Unity queue: {selectedSong.SongName}");
                Debug.Log($"[TRACKQUEUE] Queue count before adding: {queueList.Count}");
                StartCoroutine(AddSongToQueueWithPath(selectedSong.SongName, audioPath, selectedSong.SongLength, false));

                Debug.Log($"[TRACKQUEUE] Successfully added {selectedSong.SongName} to Unity queue from MongoDB");
                albumManager.UpdateDebugText($"Added {selectedSong.SongName} to Unity queue from MongoDB");
            }
            else
            {
                Debug.Log($"[TRACKQUEUE] Slave - song will be added to Unity queue after master validation");
                albumManager.UpdateDebugText($"Slave - waiting for master validation of {selectedSong.SongName}");
            }
    }
    catch (Exception ex)
    {
        albumManager.UpdateDebugText($"Error adding song to Unity queue: {ex.Message}");
        Debug.LogError($"Error adding song to Unity queue: {ex.Message}");
    }
}

// New method: Add song to Unity queue using the actual file path
public IEnumerator AddSongToQueueWithPath(string songName, string audioPath, float length = 0f, bool isFromSlave = false)
{
    Debug.Log($"[TRACKQUEUE] AddSongToQueueWithPath called - Song: {songName}, Path: {audioPath}, Length: {length}, FromSlave: {isFromSlave}");
    albumManager.UpdateDebugText($"Adding song to queue: {songName}");

    if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
    {
        Debug.LogError($"[TRACKQUEUE] Audio file does not exist: {audioPath}");
        albumManager.UpdateDebugText($"Audio file does not exist: {audioPath}");
        yield break;
    }

    if (SongPrefab == null || SongContainer == null)
    {
        Debug.LogError($"[TRACKQUEUE] SongPrefab or SongContainer is null! SongPrefab: {SongPrefab}, SongContainer: {SongContainer}");
        albumManager.UpdateDebugText("SongPrefab or SongContainer is null!");
        yield break;
    }

    Debug.Log($"[TRACKQUEUE] Creating song instance: {songName} with path: {audioPath}");

    Song songInstance = Instantiate(SongPrefab, SongContainer);
    songInstance.Initialize(songName, "Unknown Artist", audioPath, songName); // Use songName as identifier
    
    // Load the audio clip to get the actual length
    Debug.Log($"[TRACKQUEUE] Loading audio clip for length calculation: {songName}");
    yield return StartCoroutine(songInstance.LoadAudioClipFromPath());
    
    if (songInstance.AudioClip != null)
    {
        songInstance.SongLength = songInstance.AudioClip.length;
        Debug.Log($"[TRACKQUEUE] Song length set to: {songInstance.SongLength} seconds");
    }
    else
    {
        Debug.LogError($"[TRACKQUEUE] Failed to load audio clip for: {songName}");
        albumManager.UpdateDebugText($"Failed to load audio for: {songName}");
        Destroy(songInstance.gameObject);
        yield break;
    }
    
    queueList.Add((songInstance, songInstance.gameObject));
    
    // Start cooldown timer if this is a slave
    if (albumManager.isSlave && mongoDBSlaveController != null)
    {
        mongoDBSlaveController.StartCooldownTimer();
    }
    
    Debug.Log($"[TRACKQUEUE] Added song to queue list: {songName} (GameObject: {songInstance.gameObject.name})");
    Debug.Log($"[TRACKQUEUE] Total songs in queue: {queueList.Count}");
    
    // Debug: List all songs in queue after adding
    Debug.Log($"[TRACKQUEUE] Current queue contents:");
    for (int i = 0; i < queueList.Count; i++)
    {
        var (song, gameObject) = queueList[i];
        Debug.Log($"[TRACKQUEUE]   {i + 1}. {song?.SongName} (GameObject: {gameObject?.name})");
    }
    
    // Start playback if not already playing
    if (!isPlaying && !albumManager.isSlave)
    {
        Debug.Log($"[TRACKQUEUE] Starting playback from AddSongToQueueWithPath - Queue count: {queueList.Count}");
        PlayQueue();
    }
}

    private IEnumerator PlaySongQueue()
    {
        while (queueList.Count > 0)
        {
            currentSongIndex = 0;
            Song nextSong = queueList[currentSongIndex].Item1;

            albumManager.UpdateDebugText($"Loading song: {nextSong.SongName}");


            albumManager.UpdateDebugText("Checking AudioClip...");
            AudioClip clip = nextSong.GetAudioClip();
            if (clip == null)
            {
                albumManager.UpdateDebugText("Error: AudioClip is NULL. Skipping song.");
                Debug.Log($"[TRACKQUEUE] Removing song at index {currentSongIndex} due to NULL AudioClip");
                queueList.RemoveAt(currentSongIndex);
                Debug.Log($"[TRACKQUEUE] Queue count after removal: {queueList.Count}");
                continue;
            }

            albumManager.UpdateDebugText("Setting up AudioSource...");
            audioSource.clip = clip;
            if (albumManager.isSlave) audioSource.volume = 0;

            albumManager.UpdateDebugText("Playing song...");
            audioSource.Play();
            audioSource.loop = false;
            PlayedSongName.text = nextSong.SongName;
            
            // Set up time display
            UpdateUI();

            // Notify MongoDB that song is playing
            if (mongoDBMasterController != null)
            {
                // Find the corresponding tracklist entry and mark as playing
                _ = NotifyMongoDBSongPlaying(nextSong.SongName);
            }

        

            // Start playback
            Debug.Log($"[TRACKQUEUE] Starting audio playback: {nextSong.SongName}");
            audioSource.Play();

            // Update time display during playback
            while (audioSource.isPlaying || isPaused)
            {
                if (!isPaused)
                {
                    UpdateUI();
                }
                yield return null;
            }

            // If playback was stopped externally (pause/skip), exit loop early
            if (!isPlaying) yield break;

            // Remove song after it�s done playing
            Debug.Log($"[TRACKQUEUE] Song finished playing, removing: {queueList[currentSongIndex].Item1.SongName}");
            Destroy(queueList[currentSongIndex].Item2);
            queueList.RemoveAt(currentSongIndex);
            Debug.Log($"[TRACKQUEUE] Queue count after song finished: {queueList.Count}");
        }

        StopAllPlayback(); // Stop everything when queue is empty
    }



    private IEnumerator PlayNextSong(string keypadInput, bool isFromMaster)
    {
        slaveCurrentTime = 0;

        if (queueList.Count == 0)
        {
            albumManager.UpdateDebugText("Queue is empty.");
            yield break;
        }

        if (currentSongIndex >= 0 && currentSongIndex < queueList.Count)
        {
            albumManager.UpdateDebugText("Stopping previous song...");
            queueList[currentSongIndex].Item1.StopPlayback();
        }

        if (currentSongIndex < queueList.Count)
        {
            Song nextSong = queueList[currentSongIndex].Item1;
            albumManager.UpdateDebugText($"Loading song: {nextSong.SongName}");

            yield return nextSong.StartCoroutine(nextSong.LoadAudioClipFromPath());

            albumManager.UpdateDebugText("Checking AudioClip...");
            AudioClip clip = nextSong.GetAudioClip();
            if (clip == null)
            {
                albumManager.UpdateDebugText("Error: AudioClip is NULL. " + nextSong.AudioClipPath);
                yield break;
            }

            albumManager.UpdateDebugText("Setting up AudioSource...");
            audioSource.clip = clip;

            if (albumManager.isSlave)
            {
                audioSource.volume = 0;
            }

            albumManager.UpdateDebugText("Playing song...");
            audioSource.Play();
            audioSource.loop = false;

            PlayedSongName.text = nextSong.SongName;

            StartCoroutine(WaitForSongToEnd());

            if (!albumManager.isSlave && isFromMaster)
            {
                // Send song length to slave when it's a master request
                masterNetworkHandler.SendSongWithLengthToSlave(keypadInput, clip.length);
            }
            else
            {
                // If not from master, just send the length
                if (!albumManager.isSlave && !isFromMaster)
                {
                    masterNetworkHandler.SendSongLengthToSlave(clip.length);
                }
            }

            currentSongIndex++;
        }
        else
        {
            albumManager.UpdateDebugText("No more songs in the queue.");
            PlayedSongName.text = "";
        }
    }




    private IEnumerator WaitForSongToEnd()
    {
        while (audioSource.isPlaying)
        {
            yield return null;
        }

        if (queueList.Count > 0)
        {
            queueList.RemoveAt(0);
        }

        if (queueList.Count > 0)  // Check before calling PlayNextSong
        {
            currentSongIndex = 0;
            StartCoroutine(PlayNextSong("",false));
        }
        else
        {
            albumManager.UpdateDebugText("Queue is empty. No more songs to play.");
            PlayedSongName.text = "";
        }
    }


    public void PlayPreviousSong()
    {
        if (currentSongIndex > 0)
        {
            currentSongIndex -= 2;
            StartCoroutine(PlayNextSong("", false));
        }
    }

 /*   public void PlayNextSongManually()
    {
        StopCoroutine(PlayNextSong("", false));
        StartCoroutine(PlayNextSong("", false));
*//*
    }*/
    public void SetSongLength(int length)
    {
        queueList[currentSongIndex].Item1.SongLength = length;
    }



    public void PlayQueue()
    {
        Debug.Log($"[TRACKQUEUE] PlayQueue called - isPlaying: {isPlaying}, queueCount: {queueList.Count}");
        
        if (isPlaying) 
        {
            Debug.Log("[TRACKQUEUE] Already playing, skipping PlayQueue");
            return; 
        }

        if (queueList.Count == 0)
        {
            Debug.LogWarning("[TRACKQUEUE] Cannot play queue - queue is empty");
            return;
        }

        isPlaying = true;
        Debug.Log("[TRACKQUEUE] Starting PlaySongQueue coroutine");
        playbackCoroutine = StartCoroutine(PlaySongQueue());
    }

    public void PauseResumeSong()
    {
       if(isPlaying)
        {
            if (audioSource.isPlaying)
            {
                isPaused = true;
                audioSource.Pause();
                albumManager.UpdateDebugText("Playback paused.");
            }
            else if (isPaused)
            {
                isPaused = false;
                audioSource.Play();
                albumManager.UpdateDebugText("Playback resumed.");
            }
            masterNetworkHandler.Pause_Resume();
        }
    }


    public void SkipToNextSong()
    {
        if (queueList.Count > 0)
        {
            albumManager.UpdateDebugText("Skipping to next song...");
            
            // Send WebSocket skip message to server
            if (currentSongIndex < queueList.Count)
            {
                TracklistUpdate skipUpdate = new TracklistUpdate
                {
                    operationType = "skip",
                    songTitle = queueList[currentSongIndex].Item1.SongName,
                    status = "skipped",
                    songIndex = 0
                };
                string skipMessage = JsonUtility.ToJson(skipUpdate);
                SendWebSocketMessage(skipMessage);
                Debug.Log($"[TRACKQUEUE] Sent skip WebSocket message for: {queueList[currentSongIndex].Item1.SongName}");
            }

            audioSource.Stop(); // Stop current song
            isPaused = false;   // Ensure resume doesn�t interfere

            Destroy(queueList[currentSongIndex].Item2);
            queueList.RemoveAt(0); // Remove current song from queue

            if (queueList.Count > 0)
            {
                // Restart playback with next song
                StopCoroutine(playbackCoroutine);
                playbackCoroutine = StartCoroutine(PlaySongQueue());
            }
            else
            {
                StopAllPlayback(); // If no songs left, stop everything
            }
            masterNetworkHandler.PlayNextSong();
        }
    }

    public void SkipSongSlave()
    {
        // Prevent duplicate skip operations
        if (isSkipping)
        {
            Debug.Log($"[TRACKQUEUE] Skip already in progress - ignoring duplicate skip");
            return;
        }
        
        isSkipping = true;
        Debug.Log($"[TRACKQUEUE] SkipSongSlave called - queueCount: {queueList.Count}, currentSongIndex: {currentSongIndex}");
        albumManager.UpdateDebugText("Skipping to next song (Slave)...");
        
        // Set skip time for cooldown
        lastSkipTime = Time.time;
        Debug.Log($"[TRACKQUEUE] Slave skip occurred - setting cooldown until {lastSkipTime + skipCooldownDuration}");

        if (queueList.Count > 1) // Ensure there�s a next song available
        {
            // Remove current song using currentSongIndex
            if (currentSongIndex >= 0 && currentSongIndex < queueList.Count)
            {
                Destroy(queueList[currentSongIndex].Item2);
                queueList.RemoveAt(currentSongIndex);
            }

            // Reset timer and start playback of next song
            slaveCurrentTime = 0; // Reset timer
            currentSongIndex = 0; // Reset to first song (which is now the next song)
            isSlavePlaying = true; // Ensure playback is active
            isPaused = false; // Ensure not paused
            wasPaused = false; // Reset pause flag
            
            float nextSongLength = queueList[0].Item1.SongLength; // Get next song length
            timeText.text = FormatTime(0) + "/" + FormatTime(nextSongLength);
            albumManager.UpdateDebugText($"Now playing (Slave): {queueList[0].Item1.SongName}");
            
            // Stop any existing playback coroutine before starting new one
            StopAllCoroutines();
            Debug.Log($"[TRACKQUEUE] Stopped all existing coroutines before starting new playback");
            
            // Start playback simulation for the next song
            Debug.Log($"[TRACKQUEUE] About to start SimulateSlavePlayback with duration: {nextSongLength}");
            Debug.Log($"[TRACKQUEUE] Current state - currentSongIndex: {currentSongIndex}, queueCount: {queueList.Count}, isSlavePlaying: {isSlavePlaying}");
            StartCoroutine(SimulateSlavePlayback((int)nextSongLength));
            Debug.Log($"[TRACKQUEUE] Started playback of next song after skip - Duration: {nextSongLength}s, isSlavePlaying: {isSlavePlaying}");
        }
        else
        {
            // Remove current song using currentSongIndex
            if (currentSongIndex >= 0 && currentSongIndex < queueList.Count)
            {
                Destroy(queueList[currentSongIndex].Item2);
                queueList.RemoveAt(currentSongIndex);
            }
            
            // Stop playback and reset state
            isSlavePlaying = false;
            isPaused = false;
            wasPaused = false; // Reset pause flag
            slaveCurrentTime = 0;
            currentSongIndex = 0;
            
            if (timeText != null)
            {
                timeText.text = FormatTime(0);
            }
            
            albumManager.UpdateDebugText("No more songs in the queue (Slave).");
            StopAllPlayback(); // Stop UI updates since there are no more songs
        }
        
        // Reset skip flag after a short delay
        StartCoroutine(ResetSkipFlag());
    }
    
    private IEnumerator ResetSkipFlag()
    {
        yield return new WaitForSeconds(0.5f); // 500ms cooldown
        isSkipping = false;
        Debug.Log($"[TRACKQUEUE] Skip flag reset - ready for next skip operation");
    }

    private void StopAllPlayback()
    {
        // Don't stop slave playback if hub just reconnected - preserve slave state
        if (albumManager != null && albumManager.isSlave && isSlavePlaying && queueList.Count > 0)
        {
            Debug.Log("[TRACKQUEUE] Preventing stop of slave playback - hub may have reconnected");
            return;
        }
        
        isPlaying = false;
        isPaused = false;
        audioSource.Stop();
        queueList.Clear();
        albumManager.UpdateDebugText("Queue is empty. Stopping playback.");
        PlayedSongName.text = "";
    }
    // Safely run a Task from non-async code (fire-and-forget but observes exceptions)
    private async void FireAndForget(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Background task error: {ex}");
        }
    }

    private async Task NotifyMongoDBSongPlaying(string songName)
    {
        try
        {
            if (mongoDBManager == null) return;

            // Find the tracklist entry for this song
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();
            var playingSong = queuedSongs.FirstOrDefault(s => s.Title == songName);
            
            if (playingSong != null)
            {
                await mongoDBManager.UpdateTracklistStatusAsync(playingSong.Id, TracklistStatus.Playing);
                albumManager.UpdateDebugText($"Marked {songName} as playing in MongoDB");
            }
        }
        catch (Exception ex)
        {
            albumManager.UpdateDebugText($"Error notifying MongoDB of song playing: {ex.Message}");
        }
    }

    private async Task NotifyMongoDBSongFinished(string songName)
    {
        try
        {
            if (mongoDBManager == null) return;

            // Find the tracklist entry for this song
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            var finishedSong = playingSongs.FirstOrDefault(s => s.Title == songName);
            
            if (finishedSong != null)
            {
                await mongoDBManager.MarkSongAsPlayedAsync(finishedSong.Id);
                albumManager.UpdateDebugText($"Marked {songName} as played in MongoDB");
            }
        }
        catch (Exception ex)
        {
            albumManager.UpdateDebugText($"Error notifying MongoDB of song finished: {ex.Message}");
        }
    }
    
    private IEnumerator MonitorTcpConnection()
    {
        // Wait a moment for SlaveController to initialize
        yield return new WaitForSeconds(2f);
        
        // Check initial connection status
        bool initiallyConnected = CheckTcpConnection();
        wasTcpConnected = initiallyConnected;
        
        // Track how long we've been disconnected
        float disconnectedTime = 0f;
        const float disconnectTimeout = 30f; // Only clear queue after 30 seconds of disconnection
        
        while (albumManager != null && albumManager.isSlave)
        {
            yield return new WaitForSeconds(1f); // Check every second
            
            bool currentlyConnected = CheckTcpConnection();
            
            if (currentlyConnected)
            {
                // Connection is active - reset disconnect timer
                if (!wasTcpConnected)
                {
                    Debug.Log("[TRACKQUEUE] TCP connection re-established");
                }
                disconnectedTime = 0f;
            }
            else
            {
                // Connection is lost - increment disconnect timer
                disconnectedTime += 1f;
                
                // Only clear queue if we've been disconnected for a significant time
                // This prevents clearing during brief connection hiccups
                if (wasTcpConnected && disconnectedTime >= disconnectTimeout)
                {
                    Debug.Log($"[TRACKQUEUE] TCP connection lost for {disconnectedTime}s - clearing queue");
                    ClearQueueOnDisconnection();
                    disconnectedTime = 0f; // Reset to prevent repeated clearing
                }
            }
            
            wasTcpConnected = currentlyConnected;
        }
    }
    
    private bool CheckTcpConnection()
    {
        if (slaveController == null) return false;
        
        try
        {
            // Use reflection to access the private 'client' field
            var clientField = typeof(SlaveController).GetField("client", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (clientField != null)
            {
                var client = clientField.GetValue(slaveController) as TcpClient;
                
                if (client != null && client.Connected)
                {
                    return true;
                }
            }
        }
        catch (System.Exception ex)
        {
            // Silently fail - connection check failed
        }
        
        return false;
    }
    
    private void ClearQueueOnDisconnection()
    {
        if (queueList.Count > 0)
        {
            Debug.Log($"[TRACKQUEUE] Clearing {queueList.Count} songs from queue due to TCP disconnection");
            
            // Destroy all song GameObjects
            foreach (var (song, gameObject) in queueList)
            {
                if (gameObject != null)
                {
                    Destroy(gameObject);
                }
            }
            
            // Clear the queue list
            queueList.Clear();
            
            // Stop any ongoing playback
            if (isSlavePlaying)
            {
                isSlavePlaying = false;
            }
            
            if (playbackCoroutine != null)
            {
                StopCoroutine(playbackCoroutine);
                playbackCoroutine = null;
            }
        }
    }
    
    private void OnDestroy()
    {
        Debug.Log("[TRACKQUEUE] TrackQueueManager OnDestroy - Cleaning up...");
        
        if (tcpConnectionMonitorCoroutine != null)
        {
            StopCoroutine(tcpConnectionMonitorCoroutine);
        }
        
        if (playbackCoroutine != null)
        {
            StopCoroutine(playbackCoroutine);
        }
        
        // Stop all coroutines including monitoring
        StopAllCoroutines();
        
        // Clear all song GameObjects from the queue
        ClearSongQueue();
        
        Debug.Log("[TRACKQUEUE] TrackQueueManager cleanup completed");
    }
    
    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            Debug.Log("[TRACKQUEUE] Application paused - Pausing playback only");
            // Only pause playback, don't clear the queue
            if (isPlaying && !isPaused)
            {
                PauseResumeSong();
            }
        }
    }
    
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            Debug.Log("[TRACKQUEUE] Application lost focus - Pausing playback only");
            // Only pause playback, don't clear the queue
            if (isPlaying && !isPaused)
            {
                PauseResumeSong();
            }
        }
    }
    
    private void ClearSongQueue()
    {
        Debug.Log($"[TRACKQUEUE] ClearSongQueue called - Current queue count: {queueList.Count}");
        
        // Don't clear slave queue if slave is playing - preserve slave state when hub reconnects
        if (albumManager != null && albumManager.isSlave && isSlavePlaying && queueList.Count > 0)
        {
            Debug.Log("[TRACKQUEUE] Preventing clear of slave queue - slave is currently playing");
            return;
        }
        
        if (queueList.Count == 0)
        {
            Debug.LogWarning("[TRACKQUEUE] Queue is already empty - nothing to clear");
            return;
        }
        
        // Log details about each song being cleared
        for (int i = 0; i < queueList.Count; i++)
        {
            var (song, gameObject) = queueList[i];
            Debug.Log($"[TRACKQUEUE] Clearing song {i + 1}: {song?.SongName} (GameObject: {gameObject?.name})");
        }
        
        // Destroy all song GameObjects
        foreach (var (song, gameObject) in queueList)
        {
            if (gameObject != null)
            {
                Debug.Log($"[TRACKQUEUE] Destroying GameObject: {gameObject.name}");
                Destroy(gameObject);
            }
            else
            {
                Debug.LogWarning($"[TRACKQUEUE] GameObject is null for song: {song?.SongName}");
            }
        }
        
        // Clear the queue list
        queueList.Clear();
        
        Debug.Log("[TRACKQUEUE] Song queue cleared successfully");
    }
    
    #region WebSocket Integration
    
    private void InitializeWebSocketClient()
    {
        Debug.Log("[TRACKQUEUE] Initializing WebSocket client for real-time updates...");
        
        webSocketClient = FindObjectOfType<WebSocketSlaveClient>();
        if (webSocketClient == null)
        {
            // Create WebSocket client if it doesn't exist
            GameObject wsClientGO = new GameObject("WebSocketSlaveClient");
            webSocketClient = wsClientGO.AddComponent<WebSocketSlaveClient>();
            Debug.Log("[TRACKQUEUE] Created new WebSocketSlaveClient component");
        }
        
        // Subscribe to WebSocket events
        webSocketClient.OnTracklistUpdate += OnWebSocketTracklistUpdate;
        webSocketClient.OnConnected += OnWebSocketConnected;
        webSocketClient.OnDisconnected += OnWebSocketDisconnected;
        webSocketClient.OnError += OnWebSocketError;
        
        Debug.Log("[TRACKQUEUE] WebSocket client initialized successfully");
    }
    
    private void OnWebSocketTracklistUpdate(TracklistUpdate update)
    {
        messageCounter++;
        Debug.Log($"[TRACKQUEUE] Received WebSocket tracklist update #{messageCounter}: {update.operationType} - {update.songTitle} - Status: {update.status}");
        Debug.Log($"[TRACKQUEUE] Full message details: operationType={update.operationType}, status={update.status}, songTitle={update.songTitle}, songId={update.songId}");
        Debug.Log($"[TRACKQUEUE] Queue size before adding message: {webSocketMessageQueue.Count}");
        
        // Queue the message to be processed on the main thread
        webSocketMessageQueue.Enqueue(update);
        
        Debug.Log($"[TRACKQUEUE] Queue size after adding message: {webSocketMessageQueue.Count}");
    }
    
    private void HandleWebSocketPause()
    {
        Debug.Log("[TRACKQUEUE] WebSocket pause command received");
        Debug.Log($"[TRACKQUEUE] Current state - isSlave: {albumManager.isSlave}, isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}");
        Debug.Log($"[TRACKQUEUE] Additional state - wasPaused: {wasPaused}, queueCount: {queueList.Count}, slaveCurrentTime: {slaveCurrentTime}");
        
        if (albumManager.isSlave && isSlavePlaying)
        {
            isPaused = true;
            wasPaused = true; // Mark that song was paused, not finished
            Debug.Log($"[TRACKQUEUE] Slave playback paused via WebSocket - isPaused set to: {isPaused}, wasPaused set to: {wasPaused}");
        }
        else if (!albumManager.isSlave && audioSource.isPlaying)
        {
            audioSource.Pause();
            Debug.Log("[TRACKQUEUE] Master audio paused via WebSocket");
        }
        else
        {
            Debug.Log($"[TRACKQUEUE] Pause command ignored - isSlave: {albumManager.isSlave}, isSlavePlaying: {isSlavePlaying}, audioSource.isPlaying: {audioSource?.isPlaying}");
            Debug.Log($"[TRACKQUEUE] Pause ignored details - wasPaused: {wasPaused}, queueCount: {queueList.Count}, slaveCurrentTime: {slaveCurrentTime}");
        }
    }
    
    private void HandleWebSocketResume()
    {
        Debug.Log("=== [TRACKQUEUE] WebSocket resume command received ===");
        Debug.Log($"[TRACKQUEUE] Current state - isSlave: {albumManager.isSlave}, isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}");
        Debug.Log($"[TRACKQUEUE] Additional state - wasPaused: {wasPaused}, queueCount: {queueList.Count}, slaveCurrentTime: {slaveCurrentTime}");
        
        if (albumManager.isSlave)
        {
            Debug.Log($"[TRACKQUEUE] Slave resume check - isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}, wasPaused: {wasPaused}, queueCount: {queueList.Count}");
            
            if (isSlavePlaying && isPaused)
            {
                Debug.Log("[TRACKQUEUE] CASE 1: Currently playing but paused - resuming immediately");
                isPaused = false;
                wasPaused = false; // Reset pause flag
                Debug.Log($"[TRACKQUEUE] Slave playback resumed via WebSocket - isPaused set to: {isPaused}, wasPaused set to: {wasPaused}");
            }
            else if (!isSlavePlaying && wasPaused && queueList.Count > 0)
            {
                Debug.Log("[TRACKQUEUE] CASE 2: Not playing but was paused - resuming from where we left off");
                Debug.Log($"[TRACKQUEUE] Resuming from time: {slaveCurrentTime}s");
                // If not playing but was paused (not finished), resume from where we left off
                StopAllCoroutines(); // Stop any existing coroutines first
                isSlavePlaying = true;
                isPaused = false;
                wasPaused = false; // Reset pause flag
                StartCoroutine(SimulateSlavePlaybackFromTime((int)queueList[0].Item1.SongLength, slaveCurrentTime));
                Debug.Log($"[TRACKQUEUE] Slave playback resumed from pause via WebSocket - isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}");
            }
            else if (!isSlavePlaying && !wasPaused && queueList.Count > 0)
            {
                Debug.Log("[TRACKQUEUE] CASE 3: Not playing and wasn't paused - starting fresh");
                // If not playing and wasn't paused (song finished), start new playback
                StopAllCoroutines(); // Stop any existing coroutines first
                isSlavePlaying = true;
                isPaused = false;
                slaveCurrentTime = 0f;
                currentSongIndex = 0;
                StartCoroutine(SimulateSlavePlayback((int)queueList[0].Item1.SongLength));
                Debug.Log($"[TRACKQUEUE] Slave playback started fresh via WebSocket resume - isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}");
            }
            else
            {
                Debug.Log($"[TRACKQUEUE] CASE 4: Resume command ignored - isSlavePlaying: {isSlavePlaying}, isPaused: {isPaused}, wasPaused: {wasPaused}, queueCount: {queueList.Count}");
            }
        }
        else if (!albumManager.isSlave && !audioSource.isPlaying)
        {
            audioSource.Play();
            Debug.Log("[TRACKQUEUE] Master audio resumed via WebSocket");
        }
        
        Debug.Log("=== [TRACKQUEUE] Resume command processing complete ===");
    }
    
    private void HandleWebSocketSkip(int? songIndex)
    {
        Debug.Log($"[TRACKQUEUE] WebSocket skip command received (songIndex: {songIndex})");
        Debug.Log($"[TRACKQUEUE] Current state before skip - isSlave: {albumManager.isSlave}, isSlavePlaying: {isSlavePlaying}, queueCount: {queueList.Count}");
        
        if (albumManager.isSlave)
        {
            Debug.Log($"[TRACKQUEUE] Calling SkipSongSlave()");
            SkipSongSlave();
            Debug.Log($"[TRACKQUEUE] SkipSongSlave() completed - isSlavePlaying: {isSlavePlaying}, queueCount: {queueList.Count}");
        }
        else
        {
            SkipToNextSong();
        }
    }
    
    private void HandleWebSocketInsert(TracklistUpdate update)
    {
        Debug.Log($"[TRACKQUEUE] WebSocket insert/update command received: {update.songTitle} - {update.status}");
        Debug.Log($"[TRACKQUEUE] Song data - Duration: {update.duration}, Artist: {update.artist}, Album: {update.album}, ExistsAtMaster: {update.existsAtMaster}");
        
        if (albumManager.isSlave)
        {
            // If status is "playing", resume playback
            if (update.status == "playing")
            {
                Debug.Log($"[TRACKQUEUE] Received playing status - resuming playback");
                albumManager.UpdateDebugText("Resuming playback");
                HandleWebSocketResume();
                return;
            }
            
            // Check if we're in skip cooldown period (only for non-master songs to prevent rapid skips)
            // Allow master songs through even during cooldown
            if (update.masterId != "master")
            {
                float timeSinceLastSkip = Time.time - lastSkipTime;
                if (timeSinceLastSkip < skipCooldownDuration)
                {
                    Debug.Log($"[TRACKQUEUE] In skip cooldown period ({timeSinceLastSkip:F1}s / {skipCooldownDuration}s) - ignoring insertion: {update.songTitle}");
                    return;
                }
            }
            
            // Add song if:
            // 1. It's validated by master (existsAtMaster = true), OR
            // 2. It's from the master (masterId = "master"), OR  
            // 3. It's from the slave itself (masterId = "slave")
            bool shouldAdd = false;
            string reason = "";
            
            if (update.existsAtMaster)
            {
                shouldAdd = true;
                reason = "validated by master";
            }
            else if (update.masterId == "master")
            {
                shouldAdd = true;
                reason = "added by master";
            }
            else if (update.masterId == "slave")
            {
                shouldAdd = true;
                reason = "added by slave";
            }
            
            if (!shouldAdd)
            {
                Debug.Log($"[TRACKQUEUE] Song not eligible for addition (existsAtMaster={update.existsAtMaster}, masterId={update.masterId}) - ignoring: {update.songTitle}");
                return;
            }
            
            Debug.Log($"[TRACKQUEUE] Song eligible for addition ({reason}) - adding to Unity UI: {update.songTitle}");
            
            // Simple duplicate check: if song already exists in queue, skip it
            try
            {
                if (IsSongAlreadyInQueue(update.songTitle, update.songId))
                {
                    Debug.Log($"[TRACKQUEUE] Song already exists in queue, skipping duplicate: {update.songTitle}");
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TRACKQUEUE] Error checking for duplicates: {ex.Message}");
                // Continue anyway if check fails
            }
            
            Debug.Log($"[TRACKQUEUE] Adding new song directly from WebSocket: {update.songTitle}");
            
            // Add song directly to Unity queue with complete data from WebSocket
            AddSongFromWebSocket(update);
        }
        else
        {
            Debug.Log($"[TRACKQUEUE] Not in slave mode - ignoring WebSocket insert/update");
        }
    }
    
    /// <summary>
    /// Checks if a song is in cooldown period (only for user-initiated actions)
    /// </summary>
    private bool IsSongInCooldown(string songTitle)
    {
        float currentTime = Time.time;
        float cooldownTime = 5.0f; // 5 seconds cooldown
        
        if (lastSongAddedTime.ContainsKey(songTitle))
        {
            float timeSinceLastAdded = currentTime - lastSongAddedTime[songTitle];
            if (timeSinceLastAdded < cooldownTime)
            {
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Simple check: returns true if a song with the same title or ID already exists in the queue
    /// </summary>
    private bool IsSongAlreadyInQueue(string songTitle, string songId)
    {
        // Fail-safe: if queueList is null or empty, song is not in queue
        if (queueList == null || queueList.Count == 0)
            return false;
        
        // Quick check by songId first (most reliable)
        if (!string.IsNullOrEmpty(songId))
        {
            for (int i = 0; i < queueList.Count; i++)
            {
                var (song, gameObject) = queueList[i];
                if (song != null && song.KeypadInput == songId)
                {
                    return true;
                }
            }
        }
        
        // Check by song title (case-insensitive)
        if (!string.IsNullOrEmpty(songTitle))
        {
            for (int i = 0; i < queueList.Count; i++)
            {
                var (song, gameObject) = queueList[i];
                if (song != null && song.SongName != null && 
                    song.SongName.Equals(songTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        
        return false;
    }
    
    private void CleanupOldSongTimes()
    {
        float currentTime = Time.time;
        var keysToRemove = new List<string>();
        
        foreach (var kvp in lastSongAddedTime)
        {
            if (currentTime - kvp.Value > 10.0f) // Remove entries older than 10 seconds
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            lastSongAddedTime.Remove(key);
        }
    }
    
    private void AddSongFromWebSocket(TracklistUpdate update)
    {
        try
        {
            Debug.Log($"[TRACKQUEUE] Adding song from WebSocket: {update.songTitle} (Duration: {update.duration}s)");
            
            // Final safety check: make sure this song isn't already in the queue
            try
            {
                if (IsSongAlreadyInQueue(update.songTitle, update.songId))
                {
                    Debug.Log($"[TRACKQUEUE] Duplicate detected in AddSongFromWebSocket - skipping: {update.songTitle}");
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TRACKQUEUE] Error in duplicate check: {ex.Message}");
                // Continue anyway if check fails
            }
            
            // Create a Song object with the WebSocket data
            Song song = Instantiate(SongPrefab, SongContainer);
            GameObject songGO = song.gameObject;
            
            if (song != null)
            {
                // Initialize song with WebSocket data using the Initialize method
                song.Initialize(
                    update.songTitle, 
                    update.artist ?? "Unknown Artist", 
                    "", // No audio path needed for slave simulation
                    update.songId ?? "websocket_song"
                );
                
                // Set song length
                song.SongLength = update.duration;
                Debug.Log($"[TRACKQUEUE] Set song length from WebSocket: {update.songTitle} = {update.duration} seconds, SongLength now: {song.SongLength}");
                
                // Add to Unity queue (correct order: Song, GameObject)
                queueList.Add((song, songGO));
                
                // Don't start cooldown timer for WebSocket songs (master songs)
                // Cooldown only applies to user-initiated songs
                
                // Record the time this song was added for duplicate prevention
                lastSongAddedTime[update.songTitle] = Time.time;
                
                Debug.Log($"[TRACKQUEUE] Successfully added song to Unity queue: {update.songTitle}");
                Debug.Log($"[TRACKQUEUE] Song details - Name: {song.SongName}, Length: {song.SongLength}, Artist: {song.Artist}");
                Debug.Log($"[TRACKQUEUE] Queue size: {queueList.Count}");
                
                // If this is the first song and we're not playing, start playback
                if (queueList.Count == 1 && !isSlavePlaying)
                {
                    Debug.Log($"[TRACKQUEUE] Starting playback of first song from WebSocket");
                    StartSlavePlayback();
                }
            }
            else
            {
                Debug.LogError($"[TRACKQUEUE] Failed to get Song component from prefab");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TRACKQUEUE] Error adding song from WebSocket: {ex.Message}");
        }
    }
    
    private void HandleWebSocketDelete(TracklistUpdate update)
    {
        Debug.Log($"[TRACKQUEUE] WebSocket delete command received: {update.songTitle}");
        
        if (albumManager.isSlave)
        {
            Debug.Log($"[TRACKQUEUE] Delete received - immediately skipping to next song");
            albumManager.UpdateDebugText("Song deleted - skipping to next...");
            
            // Treat delete message as a skip command - immediately skip to next song
            Debug.Log($"[TRACKQUEUE] Calling SkipSongSlave() for delete message");
            SkipSongSlave();
        }
        else
        {
            Debug.Log($"[TRACKQUEUE] Not in slave mode - ignoring WebSocket delete");
        }
    }
    
    private void HandleWebSocketSkipToSong(TracklistUpdate update)
    {
        Debug.Log($"[TRACKQUEUE] WebSocket skip to song: {update.songTitle}");
        
        if (albumManager.isSlave)
        {
            // Set skip time for cooldown
            lastSkipTime = Time.time;
            Debug.Log($"[TRACKQUEUE] WebSocket skip occurred - setting cooldown until {lastSkipTime + skipCooldownDuration}");
            // Find the song in the queue and move it to current position
            for (int i = 0; i < queueList.Count; i++)
            {
                if (queueList[i].Item1.SongName == update.songTitle)
                {
                    Debug.Log($"[TRACKQUEUE] Found song to skip to: {update.songTitle} at index {i}");
                    
                    // If this is not the current song, remove current and start this one
                    if (i != currentSongIndex)
                    {
                        // Remove current song if it exists
                        if (currentSongIndex >= 0 && currentSongIndex < queueList.Count)
                        {
                            Debug.Log($"[TRACKQUEUE] Removing current song at index {currentSongIndex}");
                            Destroy(queueList[currentSongIndex].Item2);
                            queueList.RemoveAt(currentSongIndex);
                            
                            // Adjust currentSongIndex if needed
                            if (i > currentSongIndex)
                            {
                                i--; // Adjust index since we removed an item before this one
                            }
                        }
                        
                        // Move the target song to the front
                        var targetSong = queueList[i];
                        queueList.RemoveAt(i);
                        queueList.Insert(0, targetSong);
                        
                        // Update currentSongIndex to 0
                        currentSongIndex = 0;
                        
                        Debug.Log($"[TRACKQUEUE] Moved song to front and set as current");
                        albumManager.UpdateDebugText($"Skipped to: {update.songTitle}");
                        
                        // Start playback of the new current song
                        if (queueList.Count > 0)
                        {
                            StartSlavePlayback();
                        }
                    }
                    else
                    {
                        Debug.Log($"[TRACKQUEUE] Song is already current - no action needed");
                    }
                    
                    break;
                }
            }
        }
        else
        {
            Debug.Log($"[TRACKQUEUE] Not in slave mode - ignoring WebSocket skip to song");
        }
    }
    
    private void StartSlavePlayback()
    {
        if (queueList.Count > 0)
        {
            Debug.Log($"[TRACKQUEUE] Starting slave playback with {queueList.Count} songs");
            Debug.Log($"[TRACKQUEUE] First song details - Name: {queueList[0].Item1.SongName}, SongLength: {queueList[0].Item1.SongLength}");
            isSlavePlaying = true;
            slaveCurrentTime = 0f;
            currentSongIndex = 0;
            
            // Start the simulation coroutine
            StartCoroutine(SimulateSlavePlayback((int)queueList[0].Item1.SongLength));
        }
    }
    
    private void OnWebSocketConnected()
    {
        Debug.Log("[TRACKQUEUE] WebSocket connected - real-time updates enabled");
    }
    
    private void OnWebSocketDisconnected()
    {
        Debug.Log("[TRACKQUEUE] WebSocket disconnected - falling back to MongoDB polling");
    }
    
    private void OnWebSocketError(string error)
    {
        Debug.LogError($"[TRACKQUEUE] WebSocket error: {error}");
    }
    
    public void SendWebSocketMessage(string message)
    {
        if (webSocketClient != null && webSocketClient.IsConnected())
        {
            webSocketClient.SendMessage(message);
        }
        else
        {
            Debug.LogWarning("[TRACKQUEUE] Cannot send WebSocket message - not connected");
        }
    }
    
    #endregion

}
