using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;

[System.Serializable]
public class TracklistUpdate
{
    public string operationType; // "pause", "resume", "skip", "insert"
    public string songTitle;
    public string status; // "paused", "playing", "skipped", "queued"
    public float? currentTime; // For sync purposes
    public int? songIndex; // For skip operations
    
    // Complete song data for "insert" operations
    public string songId;
    public string artist;
    public string album;
    public int duration; // Song length in seconds
    public int priority;
    public string requestedBy;
    public string masterId;
    public bool existsAtMaster;
}

public class WebSocketSlaveClient : MonoBehaviour
{
    [Header("WebSocket Settings")]
    public string serverUrl = "ws://localhost:3000";
    public float reconnectInterval = 5f;
    public int maxReconnectAttempts = 10;
    
    [Header("Debug")]
    public bool enableDebugLogs = true;
    
    private WebSocket webSocket;
    private bool isConnected = false;
    private bool shouldReconnect = true;
    private int reconnectAttempts = 0;
    private Coroutine reconnectCoroutine;
    
    // Events
    public System.Action<TracklistUpdate> OnTracklistUpdate;
    public System.Action OnConnected;
    public System.Action OnDisconnected;
    public System.Action<string> OnError;
    
    private TrackQueueManager trackQueueManager;
    
    void Start()
    {
        trackQueueManager = FindObjectOfType<TrackQueueManager>();
        if (trackQueueManager == null)
        {
            Debug.LogError("[WEBSOCKET_SLAVE] TrackQueueManager not found!");
            return;
        }
        
        ConnectToServer();
    }
    
    void OnDestroy()
    {
        Disconnect();
    }
    
    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            Disconnect();
        }
        else
        {
            ConnectToServer();
        }
    }
    
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && !isConnected)
        {
            ConnectToServer();
        }
    }
    
    public void ConnectToServer()
    {
        if (isConnected || webSocket != null)
        {
            LogDebug("Already connected or connection in progress");
            return;
        }
        
        try
        {
            LogDebug($"Connecting to WebSocket server: {serverUrl}");
            
            webSocket = new WebSocket(serverUrl);
            
            // Set up event handlers
            webSocket.OnOpen += OnWebSocketOpen;
            webSocket.OnMessage += OnWebSocketMessage;
            webSocket.OnError += OnWebSocketError;
            webSocket.OnClose += OnWebSocketClose;
            
            // Connect
            webSocket.Connect();
        }
        catch (Exception ex)
        {
            LogError($"Failed to create WebSocket connection: {ex.Message}");
            OnError?.Invoke(ex.Message);
        }
    }
    
    public void Disconnect()
    {
        shouldReconnect = false;
        
        if (reconnectCoroutine != null)
        {
            StopCoroutine(reconnectCoroutine);
            reconnectCoroutine = null;
        }
        
        if (webSocket != null)
        {
            LogDebug("Disconnecting from WebSocket server");
            webSocket.Close();
            webSocket = null;
        }
        
        isConnected = false;
    }
    
    private void OnWebSocketOpen(object sender, EventArgs e)
    {
        isConnected = true;
        reconnectAttempts = 0;
        shouldReconnect = true;
        
        LogDebug("WebSocket connected successfully");
        OnConnected?.Invoke();
    }
    
    private void OnWebSocketMessage(object sender, MessageEventArgs e)
    {
        try
        {
            // Check if message data is null or empty
            if (e == null || string.IsNullOrEmpty(e.Data))
            {
                LogError("Received null or empty WebSocket message");
                return;
            }
            
            LogDebug($"Received WebSocket message: {e.Data}");
            
            // Check if the message is valid JSON
            if (!IsValidJson(e.Data))
            {
                LogError($"Invalid JSON received: {e.Data}");
                return;
            }
            
            // Parse the JSON message
            TracklistUpdate update = JsonUtility.FromJson<TracklistUpdate>(e.Data);
            
            if (update != null)
            {
                LogDebug($"Parsed tracklist update: {update.operationType} - {update.songTitle}");
                OnTracklistUpdate?.Invoke(update);
            }
            else
            {
                LogError("Failed to parse tracklist update JSON - result is null");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error processing WebSocket message: {ex.Message}");
            LogError($"Stack trace: {ex.StackTrace}");
            OnError?.Invoke(ex.Message);
        }
    }
    
    private void OnWebSocketError(object sender, ErrorEventArgs e)
    {
        string errorMessage = e?.Message ?? "Unknown WebSocket error";
        LogError($"WebSocket error: {errorMessage}");
        OnError?.Invoke(errorMessage);
    }
    
    private void OnWebSocketClose(object sender, CloseEventArgs e)
    {
        isConnected = false;
        string closeCode = e?.Code.ToString() ?? "Unknown";
        string closeReason = e?.Reason ?? "No reason provided";
        LogDebug($"WebSocket closed: {closeCode} - {closeReason}");
        OnDisconnected?.Invoke();
        
        // Attempt to reconnect if we should
        if (shouldReconnect && reconnectAttempts < maxReconnectAttempts)
        {
            reconnectCoroutine = StartCoroutine(ReconnectCoroutine());
        }
    }
    
    private IEnumerator ReconnectCoroutine()
    {
        reconnectAttempts++;
        LogDebug($"Attempting to reconnect... (Attempt {reconnectAttempts}/{maxReconnectAttempts})");
        
        yield return new WaitForSeconds(reconnectInterval);
        
        if (shouldReconnect && !isConnected)
        {
            ConnectToServer();
        }
    }
    
    public void SendMessage(string message)
    {
        if (isConnected && webSocket != null)
        {
            try
            {
                webSocket.Send(message);
                LogDebug($"Sent message: {message}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to send message: {ex.Message}");
            }
        }
        else
        {
            LogError("Cannot send message - not connected to WebSocket server");
        }
    }
    
    public bool IsConnected()
    {
        return isConnected && webSocket != null && webSocket.ReadyState == WebSocketState.Open;
    }
    
    private void LogDebug(string message)
    {
        if (enableDebugLogs)
        {
            Debug.Log($"[WEBSOCKET_SLAVE] {message}");
        }
    }
    
    private void LogError(string message)
    {
        Debug.LogError($"[WEBSOCKET_SLAVE] {message}");
    }
    
    private bool IsValidJson(string jsonString)
    {
        try
        {
            // Try to parse the JSON to see if it's valid
            JsonUtility.FromJson<TracklistUpdate>(jsonString);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    // Public method to update server URL
    public void SetServerUrl(string newUrl)
    {
        serverUrl = newUrl;
        if (isConnected)
        {
            Disconnect();
            ConnectToServer();
        }
    }
}
