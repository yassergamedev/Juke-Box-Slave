using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class KeyboardButtonController : MonoBehaviour
{
    [SerializeField] Image containerBorderImage;
    [SerializeField] Image containerFillImage;
    [SerializeField] Image containerIcon;
    [SerializeField] TextMeshProUGUI containerText;
    [SerializeField] TextMeshProUGUI containerActionText;

    private void Start() {
        SetContainerBorderColor(ColorDataStore.GetKeyboardBorderColor());
        SetContainerFillColor(ColorDataStore.GetKeyboardFillColor());
        SetContainerTextColor(ColorDataStore.GetKeyboardTextColor());
        SetContainerActionTextColor(ColorDataStore.GetKeyboardActionTextColor());
    }

    public void SetContainerBorderColor(Color color) => containerBorderImage.color = color;
    public void SetContainerFillColor(Color color) => containerFillImage.color = color;
    public void SetContainerTextColor(Color color) => containerText.color = color;
    public void SetContainerActionTextColor(Color color) { 
        containerActionText.color = color;
        containerIcon.color = color;
    }

    private void DeselectButton()
    {
        // Clear EventSystem selection to prevent pointer from triggering other buttons
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }

    public void AddLetter() {
        DeselectButton();
        if(AlbumManager.Instance != null) {
            AlbumManager.Instance.AddLetter(containerText.text);
        } else {
            Debug.Log(containerText.text + " is pressed");
        }
    }
    public void DeleteLetter() { 
        DeselectButton();
        if(AlbumManager.Instance != null) {
            AlbumManager.Instance.DeleteLetter();
        } else {
            Debug.Log("Last char deleted");
        }
    }
    public void SubmitWord() {
        DeselectButton();
        if(AlbumManager.Instance != null) {
            AlbumManager.Instance.SearchSongs();
        } else {
            Debug.Log("Submitted successfully!");
        }
    }
}