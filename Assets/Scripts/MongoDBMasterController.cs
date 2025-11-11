using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Threading.Tasks;
using MongoDBModels;
using System;
using System.Linq;

public class MongoDBMasterController : MonoBehaviour
{
    [Header("MongoDB Settings")]
    public float pollInterval = 1f; // Poll MongoDB every 1 second
    
    [Header("UI Elements")]
    public Text debugText;
    public Text statusText;
    public Text currentSongText;
    public Text queueText;
    public Button refreshQueueButton;
    public Button clearQueueButton;
    public ScrollRect queueScrollRect;
    public GameObject queueItemPrefab;
    public Button cursorLockToggleButton;

    private MongoDBManager mongoDBManager;
    private AlbumManager albumManager;
    private TrackQueueManager trackQueueManager;
    private Coroutine pollingCoroutine;
    private string masterId = "master";
    private bool isConnected = false;
    private List<TracklistEntryDocument> currentQueue = new List<TracklistEntryDocument>();

    private void Start()
    {
        Debug.Log("[MONGODB_MASTER] Starting MongoDB Master Controller...");
        
        mongoDBManager = MongoDBManager.Instance;
        albumManager = FindObjectOfType<AlbumManager>();
        trackQueueManager = FindObjectOfType<TrackQueueManager>();

        Debug.Log($"[MONGODB_MASTER] MongoDBManager found: {mongoDBManager != null}");
        Debug.Log($"[MONGODB_MASTER] AlbumManager found: {albumManager != null}");
        Debug.Log($"[MONGODB_MASTER] TrackQueueManager found: {trackQueueManager != null}");

        if (mongoDBManager == null)
        {
            Debug.LogError("[MONGODB_MASTER] MongoDBManager not found! Make sure it's in the scene.");
            UpdateDebugText("MongoDBManager not found! Make sure it's in the scene.");
            return;
        }

        if (albumManager == null)
        {
            Debug.LogError("[MONGODB_MASTER] AlbumManager not found!");
            UpdateDebugText("AlbumManager not found!");
            return;
        }

        if (trackQueueManager == null)
        {
            Debug.LogError("[MONGODB_MASTER] TrackQueueManager not found!");
            UpdateDebugText("TrackQueueManager not found!");
            return;
        }

        // Setup UI
        Debug.Log("[MONGODB_MASTER] Setting up UI...");
        SetupUI();
        
        // Start polling for new songs
        Debug.Log("[MONGODB_MASTER] Starting polling...");
        Debug.Log($"[MONGODB_MASTER] AlbumBasePath: {albumManager.AlbumBasePath}");
        Debug.Log($"[MONGODB_MASTER] Total albums in manager: {albumManager.albums.Count}");
        StartPolling();
        
        UpdateDebugText("Master initialized. Connected to MongoDB.");
        isConnected = true;
        Debug.Log("[MONGODB_MASTER] Master initialized successfully and connected to MongoDB");
    }

    private void SetupUI()
    {
        if (refreshQueueButton != null)
            refreshQueueButton.onClick.AddListener(() => _ = RefreshQueue());
        
        if (clearQueueButton != null)
            clearQueueButton.onClick.AddListener(() => _ = ClearQueue());
        
        // Setup cursor lock toggle button
        if (cursorLockToggleButton != null)
        {
            cursorLockToggleButton.onClick.AddListener(ToggleCursorLock);
        }
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

    private void StartPolling()
    {
        Debug.Log("[MONGODB_MASTER] Starting polling coroutine...");
        if (pollingCoroutine != null)
        {
            Debug.Log("[MONGODB_MASTER] Stopping existing polling coroutine...");
            StopCoroutine(pollingCoroutine);
        }
        pollingCoroutine = StartCoroutine(PollForNewSongs());
        Debug.Log("[MONGODB_MASTER] Polling coroutine started successfully");
    }

    private IEnumerator PollForNewSongs()
    {
        Debug.Log("[MONGODB_MASTER] PollForNewSongs coroutine started");
        while (isConnected)
        {
            Debug.Log($"[MONGODB_MASTER] Waiting {pollInterval} seconds before next poll...");
            yield return new WaitForSeconds(pollInterval);
            Debug.Log("[MONGODB_MASTER] Polling interval reached, processing new songs...");
            _ = ProcessNewSongs();
        }
        Debug.Log("[MONGODB_MASTER] PollForNewSongs coroutine ended (isConnected = false)");
    }

    private async Task ProcessNewSongs()
    {
        try
        {
            Debug.Log("[MONGODB_MASTER] Processing new songs...");
            
            // Get all queued songs
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();
            Debug.Log($"[MONGODB_MASTER] Found {queuedSongs.Count} queued songs in MongoDB");
            
            // Filter out songs that are already in our local queue OR already in Unity's queue
            // Also process songs that need validation (ExistsAtMaster = false)
            var newSongs = queuedSongs.Where(song => 
                !currentQueue.Any(existing => existing.Id == song.Id) &&
                !IsSongAlreadyInUnityQueue(song) &&
                !song.ExistsAtMaster).ToList(); // Only process songs that haven't been validated yet

            Debug.Log($"[MONGODB_MASTER] After filtering: {newSongs.Count} new songs to process");
            Debug.Log($"[MONGODB_MASTER] Current queue size: {currentQueue.Count}");
            Debug.Log($"[MONGODB_MASTER] Unity queue size: {trackQueueManager.queueList.Count}");
            
            // Debug each song being filtered
            foreach (var song in queuedSongs)
            {
                bool inCurrentQueue = currentQueue.Any(existing => existing.Id == song.Id);
                bool inUnityQueue = IsSongAlreadyInUnityQueue(song);
                Debug.Log($"[MONGODB_MASTER] Song {song.Title}: InCurrentQueue={inCurrentQueue}, InUnityQueue={inUnityQueue}, WillProcess={!inCurrentQueue && !inUnityQueue}");
            }

            // Process new songs
            foreach (var song in newSongs)
            {
                Debug.Log($"[MONGODB_MASTER] Processing song: {song.Title} (ID: {song.Id})");
                await ProcessNewSong(song);
            }

            // Update current playing song
            await UpdateCurrentPlayingSong();
            
            // Update queue display
            await RefreshQueue();
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error processing new songs: {ex.Message}");
        }
    }

    private async Task ProcessNewSong(TracklistEntryDocument song)
    {
        try
        {
            Debug.Log($"[MONGODB_MASTER] Processing song: {song.Title} by {song.Artist}");
            
            // First verify the song exists in master's album collection
            if (!await VerifySongExistsInMaster(song))
            {
                Debug.LogWarning($"[MONGODB_MASTER] Song not found in master albums: {song.Title}");
                UpdateDebugText($"Song not found in master albums: {song.Title}");
                // Mark as skipped in MongoDB since master doesn't have it
                await mongoDBManager.UpdateTracklistStatusAsync(song.Id, TracklistStatus.Skipped);
                return;
            }

            Debug.Log($"[MONGODB_MASTER] Song verified in master albums: {song.Title}");

            // Get the actual duration of the song
            int actualDuration = await GetSongDuration(song);
            
            // Update MongoDB record to mark as validated and set actual duration
            bool updateSuccess = await mongoDBManager.UpdateTracklistValidationAsync(song.Id, true, actualDuration);
            Debug.Log($"[MONGODB_MASTER] Updated MongoDB record - ExistsAtMaster=true, Duration={actualDuration}, Success: {updateSuccess}");
            
            if (!updateSuccess)
            {
                Debug.LogError($"[MONGODB_MASTER] Failed to update MongoDB record for song: {song.Title}");
                UpdateDebugText($"Failed to update MongoDB record for song: {song.Title}");
                return;
            }

            // Add to Unity queue after validation
            Debug.Log($"[MONGODB_MASTER] Processing song: {song.Title} (Length: {song.Title.Length})");
            
            if (song.Title.Length == 5 && song.Title[2] == '-')
            {
                // It's a keypad input (DD-DD format)
                Debug.Log($"[MONGODB_MASTER] Adding keypad input to Unity queue: {song.Title}");
                _ = trackQueueManager.AddSongToUnityQueueFromMongoDB(song.Title, "master");
            }
            else
            {
                // It's a song name - find the audio path and add to Unity queue
                Debug.Log($"[MONGODB_MASTER] Adding song name to Unity queue: {song.Title}");
                
                // Find the album folder and audio file
                string albumPath = albumManager.FindAlbumFolder(song.Album);
                if (!string.IsNullOrEmpty(albumPath))
                {
                    string audioPath = albumManager.FindSongFilePath(albumPath, song.Title);
                    if (!string.IsNullOrEmpty(audioPath))
                    {
                        StartCoroutine(trackQueueManager.AddSongToQueueWithPath(song.Title, audioPath, actualDuration, false));
                    }
                    else
                    {
                        Debug.LogError($"[MONGODB_MASTER] Audio file not found for validated song: {song.Title}");
                    }
                }
                else
                {
                    Debug.LogError($"[MONGODB_MASTER] Album folder not found for validated song: {song.Album}");
                }
            }

            // Add to our tracking list
            currentQueue.Add(song);
            Debug.Log($"[MONGODB_MASTER] Added to tracking queue. Total tracked: {currentQueue.Count}");
            
            UpdateDebugText($"New song added to queue: {song.Title} by {song.Artist}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_MASTER] Error processing song {song.Title}: {ex.Message}");
            UpdateDebugText($"Error processing new song: {ex.Message}");
        }
    }

    private async Task UpdateCurrentPlayingSong()
    {
        try
        {
            var playingSongs = await mongoDBManager.GetPlayingSongsAsync();
            
            if (playingSongs.Count > 0)
            {
                var currentSong = playingSongs.First();
                if (currentSongText != null)
                {
                    currentSongText.text = $"Now Playing: {currentSong.Title} - {currentSong.Artist}";
                }
            }
            else
            {
                if (currentSongText != null)
                {
                    currentSongText.text = "No song currently playing";
                }
            }
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error updating current song: {ex.Message}");
        }
    }

    public async Task RefreshQueue()
    {
        try
        {
            var queuedSongs = await mongoDBManager.GetQueuedSongsAsync();
            currentQueue = queuedSongs.ToList();
            
            UpdateQueueDisplay(queuedSongs);
            UpdateDebugText($"Queue refreshed. {queuedSongs.Count} songs in queue.");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error refreshing queue: {ex.Message}");
        }
    }

    private void UpdateQueueDisplay(List<TracklistEntryDocument> songs)
    {
        if (queueScrollRect == null || queueItemPrefab == null) return;

        // Clear existing items
        foreach (Transform child in queueScrollRect.content)
        {
            Destroy(child.gameObject);
        }

        // Display songs
        foreach (var song in songs)
        {
            var item = Instantiate(queueItemPrefab, queueScrollRect.content);
            var text = item.GetComponentInChildren<TMP_Text>();
            if (text != null)
            {
                text.text = $"{song.Title} - {song.Artist} ({song.Status})";
            }
        }
    }

    public async Task ClearQueue()
    {
        try
        {
            await mongoDBManager.ClearTracklistAsync();
            currentQueue.Clear();
            
            // Clear local queue
            trackQueueManager.queueList.Clear();
            
            UpdateDebugText("Queue cleared");
            await RefreshQueue();
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error clearing queue: {ex.Message}");
        }
    }

    public async Task MarkSongAsPlaying(string tracklistId)
    {
        try
        {
            await mongoDBManager.UpdateTracklistStatusAsync(tracklistId, TracklistStatus.Playing, masterId);
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

    public async Task SkipCurrentSong()
    {
        try
        {
            await mongoDBManager.SkipCurrentSongAsync();
            UpdateDebugText("Current song skipped in MongoDB");
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error skipping current song: {ex.Message}");
        }
    }

    private void UpdateDebugText(string message)
    {
        if (debugText != null)
        {
            debugText.text = message;
        }
        Debug.Log($"MongoDB Master: {message}");
    }

    private async Task<bool> VerifySongExistsInMaster(TracklistEntryDocument song)
    {
        try
        {
            // Check if it's a keypad input (DD-DD format)
            if (song.Title.Contains("-") && song.Title.Length == 5)
            {
                // Parse keypad input
                if (int.TryParse(song.Title.Substring(0, 2), out int albumIndex) &&
                    int.TryParse(song.Title.Substring(3, 2), out int songIndex))
                {
                    // Check if album exists in master's collection
                    if (albumIndex > 0 && albumIndex <= albumManager.albums.Count)
                    {
                        var album = albumManager.albums[albumIndex - 1];
                        // Check if song exists in that album
                        if (songIndex > 0 && songIndex <= album.Songs.Count)
                        {
                            return true; // Song exists in master
                        }
                    }
                }
            }
            else
            {
                // Check if song exists by name in any album
                foreach (var album in albumManager.albums)
                {
                    var foundSong = album.Songs.FirstOrDefault(s => 
                        s.SongName.Equals(song.Title, StringComparison.OrdinalIgnoreCase));
                    if (foundSong != null)
                    {
                        return true; // Song exists in master
                    }
                }
            }
            
            return false; // Song not found in master
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error verifying song existence: {ex.Message}");
            return false;
        }
    }

    private async Task<int> GetSongDuration(TracklistEntryDocument song)
    {
        try
        {
            // Check if it's a keypad input (DD-DD format)
            if (song.Title.Contains("-") && song.Title.Length == 5)
            {
                // Parse keypad input
                if (int.TryParse(song.Title.Substring(0, 2), out int albumIndex) &&
                    int.TryParse(song.Title.Substring(3, 2), out int songIndex))
                {
                    // Get song from album
                    if (albumIndex > 0 && albumIndex <= albumManager.albums.Count)
                    {
                        var album = albumManager.albums[albumIndex - 1];
                        if (songIndex > 0 && songIndex <= album.Songs.Count)
                        {
                            var foundSong = album.Songs[songIndex - 1];
                            return (int)foundSong.SongLength; // Return duration in seconds
                        }
                    }
                }
            }
            else
            {
                // Find song by name in any album
                foreach (var album in albumManager.albums)
                {
                    var foundSong = album.Songs.FirstOrDefault(s => 
                        s.SongName.Equals(song.Title, StringComparison.OrdinalIgnoreCase));
                    if (foundSong != null)
                    {
                        return (int)foundSong.SongLength; // Return duration in seconds
                    }
                }
            }
            
            return 180; // Default duration if not found
        }
        catch (Exception ex)
        {
            UpdateDebugText($"Error getting song duration: {ex.Message}");
            return 180; // Default duration on error
        }
    }

    private bool IsSongAlreadyInUnityQueue(TracklistEntryDocument song)
    {
        try
        {
            Debug.Log($"[MONGODB_MASTER] Checking Unity queue for duplicate: {song.Title}");
            Debug.Log($"[MONGODB_MASTER] Unity queue has {trackQueueManager.queueList.Count} songs");
            
            // List all songs in Unity queue for debugging
            for (int i = 0; i < trackQueueManager.queueList.Count; i++)
            {
                var queueItem = trackQueueManager.queueList[i];
                Debug.Log($"[MONGODB_MASTER] Unity queue[{i}]: {queueItem.Item1.SongName}");
            }
            
            // Check if song is already in Unity's tracklist
            bool isDuplicate = trackQueueManager.queueList.Any(queueItem => 
                queueItem.Item1.SongName.Equals(song.Title, StringComparison.OrdinalIgnoreCase));
            
            Debug.Log($"[MONGODB_MASTER] Duplicate check result for '{song.Title}': {isDuplicate}");
            return isDuplicate;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_MASTER] Error checking Unity queue: {ex.Message}");
            UpdateDebugText($"Error checking Unity queue: {ex.Message}");
            return false;
        }
    }

    private void OnDestroy()
    {
        Debug.Log("[MONGODB_MASTER] OnDestroy - Stopping polling and cleaning up...");
        
        if (pollingCoroutine != null)
        {
            StopCoroutine(pollingCoroutine);
        }
        isConnected = false;
        
        // Clear the tracking queue
        currentQueue.Clear();
        
        Debug.Log("[MONGODB_MASTER] Cleanup completed");
    }
    
    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            Debug.Log("[MONGODB_MASTER] Application paused - Stopping polling");
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
            Debug.Log("[MONGODB_MASTER] Application lost focus - Stopping polling");
            isConnected = false;
            if (pollingCoroutine != null)
            {
                StopCoroutine(pollingCoroutine);
            }
        }
    }
}
