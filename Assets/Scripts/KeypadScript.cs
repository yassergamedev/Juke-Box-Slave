using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Threading.Tasks;

public class KeypadScript : MonoBehaviour
{
    [Header("Keypad Settings")]
    public List<Button> digitButtons;
    public Button clearButton;
    public Button enterButton;
    public TMP_Text outputText;

    [Header("Special UI")]
    public GameObject Config; // Reference to Config GameObject

    private string input = "";
    private const int maxInputLength = 4;

    private TrackQueueManager trackQueueManager;
    private SlaveController slaveController;
    private MasterNetworkHandler masterNetworkHandler;
    private AlbumManager albumManager;

    private void Start()
    {
        if (digitButtons == null || digitButtons.Count != 10)
        {
            Debug.LogError("Assign all 10 digit buttons (0-9) in the inspector!");
            return;
        }

        for (int i = 0; i < digitButtons.Count; i++)
        {
            int digit = i;
            digitButtons[i].onClick.AddListener(() => OnDigitButtonPressed(digit));
        }

        clearButton.onClick.AddListener(ClearInput);
        enterButton.onClick.AddListener(ValidateInput);

        ResetOutput();

        trackQueueManager = GetComponent<TrackQueueManager>();
        slaveController = GetComponentInChildren<SlaveController>();
        masterNetworkHandler = GetComponentInChildren<MasterNetworkHandler>();
        albumManager = FindAnyObjectByType<AlbumManager>();
    }

    private void DeselectButton()
    {
        // Clear EventSystem selection to prevent pointer from triggering other buttons
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }

    private void OnDigitButtonPressed(int digit)
    {
        DeselectButton();
        if (input.Length < maxInputLength)
        {
            input += digit;
            UpdateOutput();
        }
    }

    private void ClearInput()
    {
        DeselectButton();
        input = "";
        ResetOutput();
    }

    private async void ValidateInput()
    {
        DeselectButton();
        string formattedInput = FormatInputForValidation(input);

        if (formattedInput == "99-99")
        {
            Debug.Log("Activating Config screen!");
            if (Config != null)
                Config.SetActive(true);
            else
                Debug.LogWarning("Config GameObject is not assigned!");
        }
        else if (input.Length == maxInputLength)
        {
            Debug.Log("Valid input: " + formattedInput);

            // Use MongoDB-based system instead of TCP
            if (trackQueueManager != null)
            {
                await trackQueueManager.AddSongToQueue(formattedInput, "user");
                Debug.Log("Song added to MongoDB tracklist: " + formattedInput);
            }
            else
            {
                albumManager.UpdateDebugText("TrackQueueManager reference is null!");
            }
        }
        else
        {
            Debug.Log("Incomplete input: " + input);
        }

        ClearInput();
    }

    private void UpdateOutput()
    {
        outputText.text = FormatInputForDisplay(input);
    }

    private void ResetOutput()
    {
        outputText.text = "** - **";
    }

    // Original validation format (no spaces, with 0 padding) - used for validation and queue operations
    private string FormatInputForValidation(string input)
    {
        string part1 = input.Length >= 2 ? input.Substring(0, 2) : input.PadRight(2, '0').Substring(0, 2);
        string part2 = input.Length > 2 ? input.Substring(2).PadRight(2, '0') : "00";

        return $"{part1}-{part2}";
    }

    // New display format (with spaces, with ** placeholders) - used only for visual display
    private string FormatInputForDisplay(string input)
    {
        string part1;
        string part2;
        
        if (input.Length == 0)
        {
            part1 = "**";
            part2 = "**";
        }
        else if (input.Length == 1)
        {
            part1 = input + "*";
            part2 = "**";
        }
        else if (input.Length == 2)
        {
            part1 = input;
            part2 = "**";
        }
        else if (input.Length == 3)
        {
            part1 = input.Substring(0, 2);
            part2 = input.Substring(2) + "*";
        }
        else // input.Length == 4
        {
            part1 = input.Substring(0, 2);
            part2 = input.Substring(2, 2);
        }

        return $"{part1} - {part2}";
    }
}
