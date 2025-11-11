using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using System.Threading.Tasks;

public class Song : MonoBehaviour
{
    public TMP_Text songNameText;
    public TMP_Text artistText;
    public TMP_Text numberText;
    public Button playButton;
    public Button stopButton;

    public string SongName { get; private set; }
    public string Artist { get; private set; }
    public int Number { get; private set; }
    public string NumberString { get; private set; }
    public string AudioClipPath { get; private set; }

    public AudioClip AudioClip;
    private AudioSource audioSource;

    public float SongLength { get; set; }

    public string KeypadInput { get; set; }
    
    // References for MongoDB integration
    private TrackQueueManager trackQueueManager;
    private AlbumManager albumManager;
    public void Initialize(string songName, string artist, string audioClipPath, int number)
    {
        SongName = songName;
        Artist = artist;
        AudioClipPath = audioClipPath;
        Number = number;
        NumberString = number.ToString();
        UpdateUI();
        audioSource = GetComponent<AudioSource>();
        
        // Get references for MongoDB integration
        trackQueueManager = FindObjectOfType<TrackQueueManager>();
        albumManager = FindObjectOfType<AlbumManager>();
        
        // Pre-calculate keypad input for faster access
        KeypadInput = GetKeypadInputFromSong();
        
        // Disable raycast target on text components so they don't block button clicks
        DisableTextRaycastTargets();

        if (playButton != null)
        {
            playButton.onClick.AddListener(PlayAudio);
        }

        if (stopButton != null)
        {
            stopButton.onClick.AddListener(StopPlayback);
        }
    }

    public void Initialize(string songName, string artist, string audioClipPath, string number)
    {
        SongName = songName;
        Artist = artist;
        AudioClipPath = audioClipPath;
        NumberString = number;

        UpdateUI();
        audioSource = GetComponent<AudioSource>();
        
        // Get references for MongoDB integration
        trackQueueManager = FindObjectOfType<TrackQueueManager>();
        albumManager = FindObjectOfType<AlbumManager>();
        
        // Pre-calculate keypad input for faster access
        KeypadInput = GetKeypadInputFromSong();
        
        // Disable raycast target on text components so they don't block button clicks
        DisableTextRaycastTargets();

        if (playButton != null)
        {
            playButton.onClick.AddListener(PlayAudio);
        }

        if (stopButton != null)
        {
            stopButton.onClick.AddListener(StopPlayback);
        }
    }

    private void UpdateUI()
    {
        SetTextAndAdjustSize(songNameText, SongName);
        SetTextAndAdjustSize(artistText, Artist);
        SetTextAndAdjustSize(numberText, NumberString);
    }

    private void SetTextAndAdjustSize(TMP_Text textComponent, string content)
    {
        if (textComponent == null || string.IsNullOrEmpty(content)) return;

        textComponent.text = content;
    }
    
    /// <summary>
    /// Disables raycast target on all text components so they don't block button clicks
    /// </summary>
    private void DisableTextRaycastTargets()
    {
        if (songNameText != null)
        {
            songNameText.raycastTarget = false;
        }
        
        if (artistText != null)
        {
            artistText.raycastTarget = false;
        }
        
        if (numberText != null)
        {
            numberText.raycastTarget = false;
        }
    }

    public async void PlayAudio()
    {
        // Use MongoDB tracklist system instead of direct audio playback
        if (trackQueueManager != null && albumManager != null)
        {
            // Check if we're in slave mode and cooldown is active
            if (albumManager.isSlave)
            {
                var mongoDBSlaveController = FindObjectOfType<MongoDBSlaveController>();
                if (mongoDBSlaveController != null)
                {
                    bool isReady = mongoDBSlaveController.IsCooldownReady();
                    Debug.Log($"[SONG] Cooldown check - IsReady: {isReady} for song: {SongName}");
                    if (!isReady)
                    {
                        Debug.Log($"[SONG] Cooldown active - cannot add song: {SongName}");
                        albumManager.UpdateDebugText($"Cooldown active - cannot add {SongName}");
                        return;
                    }
                }
            }
            
            // Use pre-calculated keypad input or calculate it if not available
            string keypadInput = !string.IsNullOrEmpty(KeypadInput) ? KeypadInput : GetKeypadInputFromSong();
            
            if (!string.IsNullOrEmpty(keypadInput))
            {
                Debug.Log($"[SONG] Adding song to tracklist via MongoDB: {SongName} (Keypad: {keypadInput})");
                await trackQueueManager.AddSongToQueue(keypadInput, "user");
                
                // Immediately start cooldown for user-initiated adds on slave
                if (albumManager.isSlave)
                {
                    var mongoDBSlaveController = FindObjectOfType<MongoDBSlaveController>();
                    if (mongoDBSlaveController != null)
                    {
                        mongoDBSlaveController.StartCooldownTimer();
                    }
                }
            }
            else
            {
                Debug.LogWarning($"[SONG] Could not determine keypad input for song: {SongName}");
                albumManager.UpdateDebugText($"Could not add {SongName} to tracklist - keypad input not found");
            }
        }
        else
        {
            Debug.LogWarning("[SONG] TrackQueueManager or AlbumManager not found. Falling back to direct playback.");
            // Fallback to direct audio playback if MongoDB system is not available
            if (audioSource != null)
            {
                if (AudioClip != null)
                {
                    audioSource.clip = AudioClip;
                    audioSource.Play();
                }
                else
                {
                    StartCoroutine(LoadAudioClipFromPath());
                }
            }
            else
            {
                Debug.LogWarning("AudioSource is missing.");
            }
        }
    }
    
    /// <summary>
    /// Sets the keypad input for this song (useful when creating songs programmatically)
    /// </summary>
    public void SetKeypadInput(string keypadInput)
    {
        KeypadInput = keypadInput;
    }
    
    /// <summary>
    /// Converts song data to keypad input format (DD-DD) by finding the song's position in the album
    /// </summary>
    private string GetKeypadInputFromSong()
    {
        if (albumManager == null || albumManager.albums == null)
        {
            Debug.LogWarning("[SONG] AlbumManager or albums not available");
            return "";
        }
        
        // Search through all albums to find this song
        for (int albumIndex = 0; albumIndex < albumManager.albums.Count; albumIndex++)
        {
            Album album = albumManager.albums[albumIndex];
            if (album == null || album.Songs == null) continue;
            
            // Search through songs in this album
            for (int songIndex = 0; songIndex < album.Songs.Count; songIndex++)
            {
                Song song = album.Songs[songIndex];
                if (song != null && song.SongName == SongName && song.Artist == Artist)
                {
                    // Convert to 1-based indexing and format as DD-DD
                    int albumNumber = albumIndex + 1;
                    int songNumber = songIndex + 1;
                    return $"{albumNumber:D2}-{songNumber:D2}";
                }
            }
        }
        
        Debug.LogWarning($"[SONG] Song not found in any album: {SongName} by {Artist}");
        return "";
    }

    public IEnumerator LoadAudioClipFromPath()
    {
        Debug.Log($"Attempting to load audio from: {AudioClipPath}");

        AudioClipPath = AudioClipPath.Replace("\\", "/");

        if (!System.IO.File.Exists(AudioClipPath))
        {
            Debug.Log($"Error: File does not exist at {AudioClipPath}");
            yield break;
        }

        string formattedPath = "file://" + AudioClipPath.Replace("\\", "/");

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(formattedPath, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"Error: Failed to load audio. {www.error}");
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

            if (clip == null)
            {
                Debug.Log("Error: DownloadHandler returned NULL.");
                yield break;
            }

            Debug.Log($"Success: Loaded {AudioClipPath}");
            this.AudioClip = clip;

            SongLength = AudioClip.length;

            Debug.Log($"Song Length: {SongLength} seconds");
        }
    }

    public void StopPlayback()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        else
        {
            Debug.LogWarning("AudioSource is not playing or is missing.");
        }
    }

    public AudioClip GetAudioClip()
    {
        if (AudioClip == null)
        {
            Debug.LogWarning($"AudioClip for {SongName} is not loaded yet.");
        }

        return AudioClip;
    }
}
