using System.Collections;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Reflection;

public class HubStatusChecker : MonoBehaviour
{
    [Header("Connection Settings")]
    public float connectionCheckInterval = 10f; // Check connection every 10 seconds
    public float connectionTimeout = 30f; // Wait 30 seconds before showing message
    
    [Header("UI Elements")]
    public GameObject outOfOrderPanel; // Panel to show when hub is down
    public TMP_Text outOfOrderMessageText; // Text component to display the message
    public TMP_InputField outOfOrderMessageInputField; // Input field to configure the message
    
    [Header("Config Settings")]
    public string defaultOutOfOrderMessage = "System is currently out of order. Please check back later.";
    
    private AlbumManager albumManager;
    private SlaveController slaveController;
    private TrackQueueManager trackQueueManager;
    private Coroutine statusCheckCoroutine;
    private string outOfOrderMessage;
    private bool isHubConnected = false;
    private float timeSinceLastSuccessfulConnection = 0f;
    private bool hasClearedQueueForThisDisconnection = false;
    
    private void Start()
    {
        // Load configurable message from PlayerPrefs
        outOfOrderMessage = PlayerPrefs.GetString("OutOfOrderMessage", defaultOutOfOrderMessage);
        
        // Setup input field if assigned
        if (outOfOrderMessageInputField != null)
        {
            outOfOrderMessageInputField.text = outOfOrderMessage;
            outOfOrderMessageInputField.onValueChanged.AddListener(OnOutOfOrderMessageChanged);
            outOfOrderMessageInputField.onEndEdit.AddListener(OnOutOfOrderMessageEndEdit);
        }
        
        albumManager = FindObjectOfType<AlbumManager>();
        slaveController = FindObjectOfType<SlaveController>();
        trackQueueManager = FindObjectOfType<TrackQueueManager>();
        
        // Only run on slave mode
        if (albumManager != null && albumManager.isSlave)
        {
            // Hide panel initially
            if (outOfOrderPanel != null)
            {
                outOfOrderPanel.SetActive(false);
            }
            
            if (slaveController == null)
            {
                Debug.LogError("[HUB_STATUS] SlaveController not found!");
                return;
            }
            
            // Attempt initial connection check
            StartCoroutine(InitialConnectionCheck());
            
            // Start periodic connection checking
            statusCheckCoroutine = StartCoroutine(MonitorTcpConnection());
        }
    }
    
    private IEnumerator InitialConnectionCheck()
    {
        // Wait a moment for SlaveController to initialize
        yield return new WaitForSeconds(2f);
        
        // Just check initial connection status - don't attempt connection
        // Connection should be handled by SlaveController itself
        bool connected = CheckTcpConnection();
        if (connected)
        {
            timeSinceLastSuccessfulConnection = 0f;
            hasClearedQueueForThisDisconnection = false;
        }
        UpdateHubStatus(connected);
    }
    
    private IEnumerator MonitorTcpConnection()
    {
        while (true)
        {
            yield return new WaitForSeconds(connectionCheckInterval);
            
            // Just check connection status - don't attempt reconnection
            // Reconnection should be handled by SlaveController itself
            bool connected = CheckTcpConnection();
            
            if (connected)
            {
                // Connection successful - reset timer and flags
                timeSinceLastSuccessfulConnection = 0f;
                hasClearedQueueForThisDisconnection = false;
                UpdateHubStatus(true);
            }
            else
            {
                // Connection failed - increment timer
                timeSinceLastSuccessfulConnection += connectionCheckInterval;
                
                // Only show message and clear queue after timeout period
                if (timeSinceLastSuccessfulConnection >= connectionTimeout)
                {
                    UpdateHubStatus(false);
                }
            }
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
            Debug.LogError($"[HUB_STATUS] Error checking TCP connection: {ex.Message}");
        }
        
        return false;
    }
    
    private void UpdateHubStatus(bool connected)
    {
        bool statusChanged = (connected != isHubConnected);
        isHubConnected = connected;
        
        if (connected)
        {
            // Hub connected - always hide message (even if already hidden)
            if (outOfOrderPanel != null)
            {
                if (outOfOrderPanel.activeSelf)
                {
                    outOfOrderPanel.SetActive(false);
                    Debug.Log("[HUB_STATUS] Hub connected - hiding out of order message");
                }
                else if (statusChanged)
                {
                    Debug.Log("[HUB_STATUS] Hub connected - message already hidden");
                }
            }
        }
        else
        {
            // Hub disconnected - clear queue and show message (only once per disconnection)
            if (trackQueueManager != null && !hasClearedQueueForThisDisconnection)
            {
                // Clear the queue only once when we first detect disconnection after timeout
                trackQueueManager.queueList.Clear();
                hasClearedQueueForThisDisconnection = true;
                Debug.Log("[HUB_STATUS] Hub disconnected (after timeout) - cleared queue");
            }
            
            // Show message (always ensure it's shown when disconnected)
            if (outOfOrderPanel != null)
            {
                if (!outOfOrderPanel.activeSelf)
                {
                    outOfOrderPanel.SetActive(true);
                    if (outOfOrderMessageText != null)
                    {
                        outOfOrderMessageText.text = outOfOrderMessage;
                    }
                    Debug.Log("[HUB_STATUS] Hub disconnected (after timeout) - showing out of order message");
                }
            }
        }
    }
    
    private void OnOutOfOrderMessageChanged(string newValue)
    {
        if (outOfOrderPanel != null && outOfOrderPanel.activeSelf && outOfOrderMessageText != null)
        {
            outOfOrderMessageText.text = newValue;
        }
    }
    
    private void OnOutOfOrderMessageEndEdit(string newValue)
    {
        SetOutOfOrderMessage(newValue);
    }
    
    public void SetOutOfOrderMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            message = defaultOutOfOrderMessage;
        }
        
        outOfOrderMessage = message;
        PlayerPrefs.SetString("OutOfOrderMessage", message);
        PlayerPrefs.Save();
        
        if (outOfOrderPanel != null && outOfOrderPanel.activeSelf && outOfOrderMessageText != null)
        {
            outOfOrderMessageText.text = message;
        }
    }
    
    public string GetOutOfOrderMessage()
    {
        return outOfOrderMessage;
    }
    
    private void OnDestroy()
    {
        if (statusCheckCoroutine != null)
        {
            StopCoroutine(statusCheckCoroutine);
        }
    }
}
