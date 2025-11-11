using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Threading.Tasks;
using MongoDBModels;
using System;
using System.Linq;

public class MongoDBSlaveController : MonoBehaviour
{
    [Header("MongoDB Settings")]
    public float pollInterval = 2f; // Poll MongoDB every 2 seconds
    
    [Header("UI Elements")]
    public Text debugText;
    public TMP_InputField songInputField;
    public Button addSongButton;
    public Button pauseResumeButton;
    public Button nextSongButton;
    public Button previousSongButton;
    public Text statusText;
    public Text currentSongText;
    public TMP_InputField cooldownInputField;
    public Button cooldownConfirmButton;
    public TMPro.TextMeshProUGUI cooldownStatusText;
    public Button cursorLockToggleButton;

    private MongoDBManager mongoDBManager;
    private AlbumManager albumManager;
    private TrackQueueManager trackQueueManager;
    private Coroutine pollingCoroutine;
    private string slaveId;
    private WebSocketSlaveClient webSocketClient;
    private bool isWebSocketConnected = false;
    private bool isConnected = false;
    
    // Cooldown system
    private float addSongCooldown = 5f; // Default 5 seconds
    private float lastAddSongTime = 0f; // Will be set to a very old time in Start() to make cooldown ready

    private void Start()
    {
        // Load cooldown from PlayerPrefs
        addSongCooldown = PlayerPrefs.GetFloat("AddSongCooldown", 5f);
        Debug.Log($"[MONGODB_SLAVE] Loaded add song cooldown from PlayerPrefs: {addSongCooldown} seconds");
        
        // Don't initialize cooldown on startup - keep it fresh and ready
        
        // Generate unique slave ID
        slaveId = $"slave_{System.Guid.NewGuid().ToString("N")[..8]}";
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Starting MongoDB Slave Controller...");
        
        mongoDBManager = MongoDBManager.Instance;
        albumManager = FindObjectOfType<AlbumManager>();
        trackQueueManager = FindObjectOfType<TrackQueueManager>();

        Debug.Log($"[MONGODB_SLAVE_{slaveId}] MongoDBManager found: {mongoDBManager != null}");
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] AlbumManager found: {albumManager != null}");
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] TrackQueueManager found: {trackQueueManager != null}");

        if (mongoDBManager == null)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] MongoDBManager not found! Make sure it's in the scene.");
            UpdateDebugText("MongoDBManager not found! Make sure it's in the scene.");
            return;
        }

        if (albumManager == null)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] AlbumManager not found!");
            UpdateDebugText("AlbumManager not found!");
            return;
        }

        if (trackQueueManager == null)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] TrackQueueManager not found!");
            UpdateDebugText("TrackQueueManager not found!");
            return;
        }

        // Setup UI
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Setting up UI...");
        SetupUI();
        
        // Initialize WebSocket client for real-time updates
        InitializeWebSocketClient();
        
        // Start polling for commands (disabled - using WebSocket only)
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] MongoDB polling disabled - using WebSocket only");
        // StartPolling(); // Disabled - all updates come through WebSocket
        
        UpdateDebugText($"Slave {slaveId} initialized. Connected to MongoDB.");
        isConnected = true;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Slave initialized successfully and connected to MongoDB");
    }

    private void SetupUI()
    {
        if (addSongButton != null)
            addSongButton.onClick.AddListener(() => _ = AddSongToQueue());
        
        if (pauseResumeButton != null)
            pauseResumeButton.onClick.AddListener(() => _ = PauseResumeSong());
        
        if (nextSongButton != null)
            nextSongButton.onClick.AddListener(() => _ = PlayNextSong());
        
        if (previousSongButton != null)
            previousSongButton.onClick.AddListener(() => _ = PlayPreviousSong());
        
        // Setup cooldown input field and button
        if (cooldownInputField != null)
        {
            cooldownInputField.text = addSongCooldown.ToString();
        }
        
        if (cooldownConfirmButton != null)
        {
            cooldownConfirmButton.onClick.AddListener(OnCooldownConfirmClicked);
        }
        
        // Setup cursor lock toggle button
        if (cursorLockToggleButton != null)
        {
            cursorLockToggleButton.onClick.AddListener(ToggleCursorLock);
        }
        
        // Start coroutine to update cooldown status
        StartCoroutine(UpdateCooldownStatus());
    }
    
    private void OnCooldownConfirmClicked()
    {
        if (cooldownInputField != null && float.TryParse(cooldownInputField.text, out float newCooldown))
        {
            if (newCooldown >= 0 && newCooldown <= 300) // Reasonable range: 0-5 minutes
            {
                addSongCooldown = newCooldown;
                PlayerPrefs.SetFloat("AddSongCooldown", addSongCooldown);
                PlayerPrefs.Save();
                Debug.Log($"[MONGODB_SLAVE_{slaveId}] Cooldown updated and saved: {addSongCooldown} seconds");
                UpdateDebugText($"Cooldown set to {addSongCooldown} seconds");
            }
            else
            {
                Debug.LogWarning($"[MONGODB_SLAVE_{slaveId}] Invalid cooldown value: {newCooldown}. Must be between 0-300 seconds.");
                UpdateDebugText($"Invalid cooldown: {newCooldown}. Use 0-300 seconds.");
                // Reset to current value
                cooldownInputField.text = addSongCooldown.ToString();
            }
        }
        else
        {
            Debug.LogWarning($"[MONGODB_SLAVE_{slaveId}] Failed to parse cooldown value: {cooldownInputField?.text}");
            UpdateDebugText("Invalid cooldown format. Use numbers only.");
            // Reset to current value
            if (cooldownInputField != null)
                cooldownInputField.text = addSongCooldown.ToString();
        }
    }
    
    private IEnumerator UpdateCooldownStatus()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.1f); // Update every 100ms
            
            float timeSinceLastAdd = Time.time - lastAddSongTime;
            float remainingTime = addSongCooldown - timeSinceLastAdd;
            
            // Debug logging every 5 seconds
            if (Mathf.FloorToInt(Time.time) % 5 == 0 && Mathf.FloorToInt(Time.time) != Mathf.FloorToInt(Time.time - Time.deltaTime))
            {
                Debug.Log($"[MONGODB_SLAVE_{slaveId}] Cooldown debug - Time.time: {Time.time:F1}, lastAddSongTime: {lastAddSongTime:F1}, timeSinceLastAdd: {timeSinceLastAdd:F1}, addSongCooldown: {addSongCooldown}, remainingTime: {remainingTime:F1}");
            }
            
            // Determine readiness
            bool isReady = remainingTime <= 0f;
            
            // Clamp remaining time to >= 0 and display as whole seconds
            int remainingWhole = Mathf.Max(0, Mathf.CeilToInt(remainingTime));
            
            if (cooldownStatusText != null)
            {
                if (!isReady)
                {
                    cooldownStatusText.text = "Cooldown: " + remainingWhole ;
                }
                else
                {
                    cooldownStatusText.text = "Ready (Cooldown: " + addSongCooldown + ")";
                }
            }
            
            // Disable/enable input UI while on cooldown
            if (addSongButton != null)
            {
                addSongButton.interactable = isReady;
            }
            if (songInputField != null)
            {
                songInputField.interactable = isReady;
            }
        }
    }

    private void StartPolling()
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Starting polling coroutine...");
        if (pollingCoroutine != null)
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Stopping existing polling coroutine...");
            StopCoroutine(pollingCoroutine);
        }
        pollingCoroutine = StartCoroutine(PollForCommands());
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Polling coroutine started successfully");
    }

    private IEnumerator PollForCommands()
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] PollForCommands coroutine started");
        while (isConnected)
        {
            // Use different polling intervals based on WebSocket connection status
            float currentPollInterval = isWebSocketConnected ? pollInterval * 3 : pollInterval; // Slower polling when WebSocket is connected
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Waiting {currentPollInterval} seconds before next poll... (WebSocket: {isWebSocketConnected})");
            yield return new WaitForSeconds(currentPollInterval);
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Polling interval reached, checking for commands...");
            _ = CheckForCommands();
        }
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] PollForCommands coroutine ended (isConnected = false)");
    }

    private async Task CheckForCommands()
    {
        try
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Checking for commands...");
            
            // Get ALL songs from tracklist - slave processes any validated song
            var allSongs = await mongoDBManager.GetAllTracklistEntriesAsync();
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Total songs in tracklist: {allSongs.Count}");
            
            // Filter for validated songs only (ExistsAtMaster = true)
            var validatedSongs = allSongs.Where(song => song.ExistsAtMaster).ToList();
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Validated songs (ExistsAtMaster=true): {validatedSongs.Count}");
            
            // Log all validated songs for debugging
            foreach (var song in validatedSongs)
            {
                Debug.Log($"[MONGODB_SLAVE_{slaveId}] Validated song: {song.Title}, Duration: {song.Duration}, Length: {song.Length}, Status: {song.Status}");
            }
            
            // Process each validated song that's not already in Unity queue
            foreach (var song in validatedSongs)
            {
                if (!IsSongAlreadyInUnityQueue(song))
                {
                    Debug.Log($"[MONGODB_SLAVE_{slaveId}] Processing new validated song: {song.Title}");
                    await ProcessAssignedSong(song);
                }
                else
                {
                    Debug.Log($"[MONGODB_SLAVE_{slaveId}] Song already in Unity queue: {song.Title}");
                }
            }
            
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Unity queue size after processing: {trackQueueManager.queueList.Count}");
            
            // Check for control commands (pause, next, previous)
            await CheckControlCommands();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] Error polling MongoDB: {ex.Message}");
            UpdateDebugText($"Error polling MongoDB: {ex.Message}");
        }
    }

    private async Task ProcessAssignedSong(TracklistEntryDocument song)
    {
        try
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Processing validated song: {song.Title}");
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Song data - Duration: {song.Duration}, Length: {song.Length}, ExistsAtMaster: {song.ExistsAtMaster}");
            
            // Use Duration field (which contains the song duration) with Length as fallback
            float duration = song.Duration ?? song.Length ?? 180f;
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Using song duration: {duration} seconds (from Duration: {song.Duration}, Length: {song.Length})");
            
            // Add song to Unity queue for slave simulation
            StartCoroutine(AddSongToSlaveQueue(song.Title, duration));

            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Successfully added song to queue: {song.Title}");
            UpdateDebugText($"Added song to queue: {song.Title}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] Error processing validated song {song.Title}: {ex.Message}");
            UpdateDebugText($"Error processing validated song: {ex.Message}");
        }
    }

    private async Task CheckControlCommands()
    {
        // This could be expanded to check for specific control commands
        // For now, we'll rely on the local UI controls
    }

    private IEnumerator AddSongToSlaveQueue(string songName, float duration)
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] AddSongToSlaveQueue called - Song: {songName}, Duration: {duration}");
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Duration breakdown - Duration: {duration} seconds = {duration/60:F1} minutes");
        
        if (trackQueueManager.SongPrefab == null || trackQueueManager.SongContainer == null)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] SongPrefab or SongContainer is null!");
            yield break;
        }

        // Create song instance for slave simulation
        Song songInstance = Instantiate(trackQueueManager.SongPrefab, trackQueueManager.SongContainer);
        songInstance.Initialize(songName, "Unknown Artist", "", songName); // No audio path needed for slave
        songInstance.SongLength = duration; // Set duration from MongoDB
        
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Created song instance - SongLength: {songInstance.SongLength} seconds");
        
        // Add to Unity queue
        trackQueueManager.queueList.Add((songInstance, songInstance.gameObject));
        
        // Start cooldown timer when song is added
        lastAddSongTime = Time.time;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Started cooldown timer for song: {songName}");
        
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Added song to slave queue: {songName} (Duration: {duration}s = {duration/60:F1} minutes)");
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Total songs in slave queue: {trackQueueManager.queueList.Count}");
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Current isSlavePlaying status: {trackQueueManager.isSlavePlaying}");
        
        // Start slave playback simulation if not already playing
        if (!trackQueueManager.isSlavePlaying)
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Starting slave playback simulation with duration: {duration} seconds");
            trackQueueManager.isSlavePlaying = true;
            
            trackQueueManager.slaveCurrentTime = 0f;
            
            // Set currentSongIndex to 0 for the first song in slave mode
            trackQueueManager.currentSongIndex = 0;
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Set currentSongIndex to 0 for slave playback");
            
            trackQueueManager.StartCoroutine(trackQueueManager.SimulateSlavePlayback((int)duration));
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Slave playback simulation started with {duration} seconds duration");
        }
        else
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Slave is already playing, song added to queue");
        }
    }

    public async Task AddSongToQueue()
    {
        // Check cooldown
        float timeSinceLastAdd = Time.time - lastAddSongTime;
        if (timeSinceLastAdd < addSongCooldown)
        {
            float remainingTime = addSongCooldown - timeSinceLastAdd;
            UpdateDebugText($"Cooldown active. Please wait {remainingTime:F1} seconds.");
            return;
        }
        
        if (string.IsNullOrEmpty(songInputField.text))
        {
            UpdateDebugText("Please enter a song or keypad input (DD-DD)");
            return;
        }

        try
        {
            // Update last add time
            lastAddSongTime = Time.time;
            string input = songInputField.text.Trim();
            string songId = System.Guid.NewGuid().ToString();
            string title = input;
            string artist = "Unknown";
            string album = "Unknown";
            int duration = 180; // Default duration

            // Try to get song info if it's a keypad input
            if (input.Length == 5 && input[2] == '-' && 
                int.TryParse(input.Substring(0, 2), out int albumIndex) &&
                int.TryParse(input.Substring(3, 2), out int songIndex))
            {
                // It's a keypad input, try to get real song info
                if (albumManager.albums.Count > albumIndex - 1)
                {
                    var albumObj = albumManager.albums[albumIndex - 1];
                    if (albumObj.Songs.Count > songIndex - 1)
                    {
                        var song = albumObj.Songs[songIndex - 1];
                        title = song.SongName;
                        artist = song.Artist;
                        album = albumObj.albumName;
                        duration = (int)song.SongLength;
                    }
                }
            }

            // Add to MongoDB tracklist
            var tracklistEntry = await mongoDBManager.AddSongToTracklistAsync(
                songId, title, artist, album, duration, slaveId, "master", 1);

            if (tracklistEntry != null)
            {
                UpdateDebugText($"Song added to MongoDB tracklist: {title}");
                songInputField.text = ""; // Clear input
            }
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error adding song to queue: {ex.Message}");
        }
    }

    public async Task PauseResumeSong()
    {
        try
        {
            // Update current playing song status in MongoDB
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            foreach (var song in playingSongs)
            {
                if (song.SlaveId == slaveId)
                {
                    // Toggle pause/resume - you might want to add a specific status for this
                    await mongoDBManager.UpdateTracklistStatusAsync(song.Id, TracklistStatus.Queued, slaveId);
                }
            }

            // Also call local pause/resume
            trackQueueManager.PauseResumeSong();
            UpdateDebugText("Pause/Resume command sent");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error with pause/resume: {ex.Message}");
        }
    }

    public async Task PlayNextSong()
    {
        try
        {
            // Mark current song as skipped in MongoDB
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            foreach (var song in playingSongs)
            {
                if (song.SlaveId == slaveId)
                {
                    await mongoDBManager.UpdateTracklistStatusAsync(song.Id, TracklistStatus.Skipped, slaveId);
                }
            }

            // Call local next song
            trackQueueManager.SkipToNextSong();
            UpdateDebugText("Next song command sent");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error with next song: {ex.Message}");
        }
    }

    public async Task PlayPreviousSong()
    {
        try
        {
            // Call local previous song
            trackQueueManager.PlayPreviousSong();
            UpdateDebugText("Previous song command sent");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error with previous song: {ex.Message}");
        }
    }

    public async Task MarkSongAsPlaying(string tracklistId)
    {
        try
        {
            await mongoDBManager.UpdateTracklistStatusAsync(tracklistId, TracklistStatus.Playing, slaveId);
            UpdateDebugText("Song marked as playing in MongoDB");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error marking song as playing: {ex.Message}");
        }
    }

    public async Task MarkSongAsPlayed(string tracklistId)
    {
        try
        {
            await mongoDBManager.MarkSongAsPlayedAsync(tracklistId);
            UpdateDebugText("Song marked as played in MongoDB");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error marking song as played: {ex.Message}");
        }
    }

    private void UpdateDebugText(string message)
    {
        if (debugText != null)
        {
            debugText.text = message;
        }
        Debug.Log($"MongoDB Slave {slaveId}: {message}");
    }

    private bool IsSongAlreadyInUnityQueue(TracklistEntryDocument song)
    {
        try
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Checking Unity queue for duplicate: {song.Title}");
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Unity queue has {trackQueueManager.queueList.Count} songs");
            
            // List all songs in Unity queue for debugging
            for (int i = 0; i < trackQueueManager.queueList.Count; i++)
            {
                var queueItem = trackQueueManager.queueList[i];
                Debug.Log($"[MONGODB_SLAVE_{slaveId}] Unity queue[{i}]: {queueItem.Item1.SongName}");
            }
            
            // Check if song is already in Unity's tracklist
            bool isDuplicate = trackQueueManager.queueList.Any(queueItem => 
                queueItem.Item1.SongName.Equals(song.Title, StringComparison.OrdinalIgnoreCase));
            
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Duplicate check result for '{song.Title}': {isDuplicate}");
            return isDuplicate;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_SLAVE_{slaveId}] Error checking Unity queue: {ex.Message}");
            UpdateDebugText($"Error checking Unity queue: {ex.Message}");
            return false;
        }
    }

    private void OnDestroy()
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] OnDestroy - Stopping polling and cleaning up...");
        
        if (pollingCoroutine != null)
        {
            StopCoroutine(pollingCoroutine);
        }
        isConnected = false;
        
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Cleanup completed");
    }
    
    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Application paused - Stopping polling");
            isConnected = false;
            if (pollingCoroutine != null)
            {
                StopCoroutine(pollingCoroutine);
            }
        }
    }
    
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
        {
            Debug.Log($"[MONGODB_SLAVE_{slaveId}] Application lost focus - Stopping polling");
            isConnected = false;
            if (pollingCoroutine != null)
            {
                StopCoroutine(pollingCoroutine);
            }
        }
    }
    
    #region WebSocket Integration
    
    private void InitializeWebSocketClient()
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Initializing WebSocket client...");
        
        webSocketClient = FindObjectOfType<WebSocketSlaveClient>();
        if (webSocketClient == null)
        {
            Debug.LogWarning($"[MONGODB_SLAVE_{slaveId}] WebSocketSlaveClient not found - real-time updates disabled");
            return;
        }
        
        // Subscribe to WebSocket events
        webSocketClient.OnTracklistUpdate += OnWebSocketTracklistUpdate;
        webSocketClient.OnConnected += OnWebSocketConnected;
        webSocketClient.OnDisconnected += OnWebSocketDisconnected;
        webSocketClient.OnError += OnWebSocketError;
        
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] WebSocket client initialized successfully");
    }
    
    private void OnWebSocketTracklistUpdate(TracklistUpdate update)
    {
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Received WebSocket tracklist update: {update.operationType} - {update.songTitle}");
        // TrackQueueManager will handle this
    }
    
    private void OnWebSocketConnected()
    {
        isWebSocketConnected = true;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] WebSocket connected - switching to reduced polling frequency");
        UpdateDebugText("WebSocket connected - real-time updates enabled");
    }
    
    private void OnWebSocketDisconnected()
    {
        isWebSocketConnected = false;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] WebSocket disconnected - switching to normal polling frequency");
        UpdateDebugText("WebSocket disconnected - using MongoDB polling only");
    }
    
    private void OnWebSocketError(string error)
    {
        Debug.LogError($"[MONGODB_SLAVE_{slaveId}] WebSocket error: {error}");
        UpdateDebugText($"WebSocket error: {error}");
    }
    
    public bool IsCooldownReady()
    {
        float timeSinceLastAdd = Time.time - lastAddSongTime;
        bool isReady = timeSinceLastAdd >= addSongCooldown;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] IsCooldownReady - Time.time: {Time.time:F1}, lastAddSongTime: {lastAddSongTime:F1}, timeSinceLastAdd: {timeSinceLastAdd:F1}, addSongCooldown: {addSongCooldown}, isReady: {isReady}");
        return isReady;
    }
    
    // Public method to start cooldown timer (called by TrackQueueManager)
    public void StartCooldownTimer()
    {
        lastAddSongTime = Time.time;
        Debug.Log($"[MONGODB_SLAVE_{slaveId}] Cooldown timer started externally - Time.time: {Time.time:F1}, lastAddSongTime: {lastAddSongTime:F1}");
    }
    
    private void ToggleCursorLock()
    {
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
    
    #endregion
}
