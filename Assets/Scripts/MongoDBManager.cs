using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Driver;
using UnityEngine;
using MongoDBModels;
public class MongoDBManager : MonoBehaviour
{
    [Header("MongoDB Settings")]
    public string connectionString = "mongodb+srv://mezragyasser2002:mezrag.yasser123...@8bbjukebox.w1btiwn.mongodb.net/";
    public string databaseName = "jukebox";
    
    [Header("Security")]
    [SerializeField] private bool useEnvironmentVariables = true;

    private MongoClient client;
    private IMongoDatabase database;
    private IMongoCollection<AlbumDocument> albumsCollection;
    private IMongoCollection<SongDocument> songsCollection;
    private IMongoCollection<TracklistEntryDocument> tracklistCollection;

    public static MongoDBManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeMongoDB();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void InitializeMongoDB()
    {
        try
        {
            client = new MongoClient(connectionString);
            database = client.GetDatabase(databaseName);
            
            albumsCollection = database.GetCollection<AlbumDocument>("albums");
            songsCollection = database.GetCollection<SongDocument>("songs");
            tracklistCollection = database.GetCollection<TracklistEntryDocument>("tracklist");

            Debug.Log("MongoDB connection initialized successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to initialize MongoDB: {ex.Message}");
        }
    }

    // AlbumDocument operations
    public async Task<List<AlbumDocument>> GetAllAlbumsAsync()
    {
        try
        {
            return await albumsCollection.Find(_ => true).ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting albums: {ex.Message}");
            return new List<AlbumDocument>();
        }
    }

    // SongDocument operations
    public async Task<List<SongDocument>> GetAllSongsAsync()
    {
        try
        {
            return await songsCollection.Find(_ => true).ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting songs: {ex.Message}");
            return new List<SongDocument>();
        }
    }

    public async Task<List<SongDocument>> GetSongsByAlbumAsync(string albumTitle)
    {
        try
        {
            return await songsCollection.Find(s => s.Album == albumTitle).ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting songs by AlbumDocument: {ex.Message}");
            return new List<SongDocument>();
        }
    }

    public async Task<SongDocument> GetSongByIdAsync(string songId)
    {
        try
        {
            return await songsCollection.Find(s => s.Id == songId).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting SongDocument by ID: {ex.Message}");
            return null;
        }
    }

    // Tracklist operations
    public async Task<List<TracklistEntryDocument>> GetQueuedSongsAsync()
    {
        try
        {
            var songs = await tracklistCollection
                .Find(t => t.Status == TracklistStatus.Queued)
                .Sort(Builders<TracklistEntryDocument>.Sort.Ascending(t => t.Priority).Ascending(t => t.CreatedAt))
                .ToListAsync();
            
            // Migrate legacy 'length' field to 'duration' field
            foreach (var song in songs)
            {
                if (song.Duration == null && song.Length != null)
                {
                    song.Duration = song.Length;
                    // Update the document in MongoDB
                    var filter = Builders<TracklistEntryDocument>.Filter.Eq(t => t.Id, song.Id);
                    var update = Builders<TracklistEntryDocument>.Update
                        .Set(t => t.Duration, song.Length)
                        .Unset(t => t.Length);
                    await tracklistCollection.UpdateOneAsync(filter, update);
                    Debug.Log($"[MONGODB_MANAGER] Migrated legacy 'length' field to 'duration' for song: {song.Title}");
                }
            }
            
            return songs;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting queued songs: {ex.Message}");
            return new List<TracklistEntryDocument>();
        }
    }

    public async Task<List<TracklistEntryDocument>> GetPlayingSongsAsync()
    {
        try
        {
            var songs = await tracklistCollection.Find(t => t.Status == TracklistStatus.Playing).ToListAsync();
            
            // Migrate legacy 'length' field to 'duration' field
            foreach (var song in songs)
            {
                if (song.Duration == null && song.Length != null)
                {
                    song.Duration = song.Length;
                    // Update the document in MongoDB
                    var filter = Builders<TracklistEntryDocument>.Filter.Eq(t => t.Id, song.Id);
                    var update = Builders<TracklistEntryDocument>.Update
                        .Set(t => t.Duration, song.Length)
                        .Unset(t => t.Length);
                    await tracklistCollection.UpdateOneAsync(filter, update);
                    Debug.Log($"[MONGODB_MANAGER] Migrated legacy 'length' field to 'duration' for song: {song.Title}");
                }
            }
            
            return songs;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting playing songs: {ex.Message}");
            return new List<TracklistEntryDocument>();
        }
    }

    public async Task<List<TracklistEntryDocument>> GetPlayedSongsAsync()
    {
        try
        {
            return await tracklistCollection
                .Find(t => t.Status == TracklistStatus.Played)
                .Sort(Builders<TracklistEntryDocument>.Sort.Descending(t => t.PlayedAt))
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting played songs: {ex.Message}");
            return new List<TracklistEntryDocument>();
        }
    }

    public async Task<TracklistEntryDocument> AddSongToTracklistAsync(string songId, string title, string artist, string AlbumDocument, int duration, string requestedBy, string masterId, int priority = 1, string status = "queued")
    {
        try
        {
            var TracklistEntryDocument = new TracklistEntryDocument
            {
                SongId = songId,
                Title = title,
                Artist = artist,
                Album = AlbumDocument,
                Duration = duration,
                Status = status,
                Priority = priority,
                CreatedAt = DateTime.UtcNow,
                PlayedAt = null,
                RequestedBy = requestedBy,
                MasterId = masterId,
                SlaveId = null,
                ExistsAtMaster = false // Default to false - will be set to true by master after validation
            };

            await tracklistCollection.InsertOneAsync(TracklistEntryDocument);
            Debug.Log($"Added song to tracklist: {title} by {artist} (ExistsAtMaster=false)");
            return TracklistEntryDocument;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error adding song to tracklist: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> UpdateTracklistStatusAsync(string tracklistId, string status, string slaveId = null)
    {
        try
        {
            var filter = Builders<TracklistEntryDocument>.Filter.Eq(t => t.Id, tracklistId);
            var update = Builders<TracklistEntryDocument>.Update
                .Set(t => t.Status, status)
                .Set(t => t.SlaveId, slaveId);

            if (status == TracklistStatus.Playing || status == TracklistStatus.Played)
            {
                update = update.Set(t => t.PlayedAt, DateTime.UtcNow);
            }

            var result = await tracklistCollection.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error updating tracklist status: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SkipCurrentSongAsync()
    {
        try
        {
            var playingSongs = await GetPlayingSongsAsync();
            foreach (var SongDocument in playingSongs)
            {
                await UpdateTracklistStatusAsync(SongDocument.Id, TracklistStatus.Skipped);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error skipping current SongDocument: {ex.Message}");
            return false;
        }
    }

    public async Task<TracklistEntryDocument> GetNextSongAsync()
    {
        try
        {
            var queuedSongs = await GetQueuedSongsAsync();
            if (queuedSongs.Count > 0)
            {
                var nextSong = queuedSongs[0];
                await UpdateTracklistStatusAsync(nextSong.Id, TracklistStatus.Playing);
                return nextSong;
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting next SongDocument: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> MarkSongAsPlayedAsync(string tracklistId)
    {
        try
        {
            return await UpdateTracklistStatusAsync(tracklistId, TracklistStatus.Played);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error marking SongDocument as played: {ex.Message}");
            return false;
        }
    }

    // Search functionality
    public async Task<List<SongDocument>> SearchSongsAsync(string query)
    {
        try
        {
            var filter = Builders<SongDocument>.Filter.Regex(s => s.Title, new MongoDB.Bson.BsonRegularExpression(query, "i"));
            return await songsCollection.Find(filter).ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error searching songs: {ex.Message}");
            return new List<SongDocument>();
        }
    }

    // Additional utility methods
    public async Task<bool> ClearTracklistAsync()
    {
        try
        {
            var result = await tracklistCollection.DeleteManyAsync(_ => true);
            Debug.Log($"Cleared {result.DeletedCount} tracklist entries");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error clearing tracklist: {ex.Message}");
            return false;
        }
    }

    public async Task<List<TracklistEntryDocument>> GetTracklistByStatusAsync(string status)
    {
        try
        {
            return await tracklistCollection
                .Find(t => t.Status == status)
                .Sort(Builders<TracklistEntryDocument>.Sort.Ascending(t => t.Priority).Ascending(t => t.CreatedAt))
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting tracklist by status: {ex.Message}");
            return new List<TracklistEntryDocument>();
        }
    }

    public async Task<bool> UpdateSongFamilyFriendlyAsync(string songId, bool familyFriendly)
    {
        try
        {
            var filter = Builders<SongDocument>.Filter.Eq(s => s.Id, songId);
            var update = Builders<SongDocument>.Update.Set(s => s.FamilyFriendly, familyFriendly);
            var result = await songsCollection.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error updating song family friendly status: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateTracklistValidationAsync(string tracklistId, bool existsAtMaster, int duration)
    {
        try
        {
            Debug.Log($"[MONGODB_MANAGER] Updating tracklist validation - ID: {tracklistId}, ExistsAtMaster: {existsAtMaster}, Duration: {duration}");
            
            var filter = Builders<TracklistEntryDocument>.Filter.Eq(t => t.Id, tracklistId);
            var update = Builders<TracklistEntryDocument>.Update
                .Set(t => t.ExistsAtMaster, existsAtMaster)
                .Set(t => t.Duration, duration);

            var result = await tracklistCollection.UpdateOneAsync(filter, update);
            Debug.Log($"[MONGODB_MANAGER] Update result - ModifiedCount: {result.ModifiedCount}, MatchedCount: {result.MatchedCount}");
            
            if (result.ModifiedCount == 0)
            {
                Debug.LogWarning($"[MONGODB_MANAGER] No documents were modified for ID: {tracklistId}");
                // Let's check if the document exists
                var existingDoc = await tracklistCollection.Find(filter).FirstOrDefaultAsync();
                if (existingDoc == null)
                {
                    Debug.LogError($"[MONGODB_MANAGER] Document with ID {tracklistId} not found in tracklist collection");
                }
                else
                {
                    Debug.Log($"[MONGODB_MANAGER] Document exists but not modified - Current ExistsAtMaster: {existingDoc.ExistsAtMaster}, Current Duration: {existingDoc.Duration}");
                }
            }
            
            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MONGODB_MANAGER] Error updating tracklist validation: {ex.Message}");
            return false;
        }
    }

    public async Task<List<TracklistEntryDocument>> GetAllTracklistEntriesAsync()
    {
        try
        {
            var songs = await tracklistCollection.Find(_ => true).ToListAsync();
            
            // Migrate legacy 'length' field to 'duration' field
            foreach (var song in songs)
            {
                if (song.Duration == null && song.Length != null)
                {
                    song.Duration = song.Length;
                    // Update the document in MongoDB
                    var filter = Builders<TracklistEntryDocument>.Filter.Eq(t => t.Id, song.Id);
                    var update = Builders<TracklistEntryDocument>.Update
                        .Set(t => t.Duration, song.Length)
                        .Unset(t => t.Length);
                    await tracklistCollection.UpdateOneAsync(filter, update);
                    Debug.Log($"[MONGODB_MANAGER] Migrated legacy 'length' field to 'duration' for song: {song.Title}");
                }
            }
            
            return songs;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting all tracklist entries: {ex.Message}");
            return new List<TracklistEntryDocument>();
        }
    }

    // Expose collections for direct access if needed
    public IMongoCollection<AlbumDocument> AlbumsCollection => albumsCollection;
    public IMongoCollection<SongDocument> SongsCollection => songsCollection;
    public IMongoCollection<TracklistEntryDocument> TracklistCollection => tracklistCollection;
}
