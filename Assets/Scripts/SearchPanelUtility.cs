using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class SearchPanelUtility : MonoBehaviour
{
    public GameObject containerToClear;
    public TMP_Text labelToClear; // assign in Inspector
    public void ClearChildrenFromContainer()
    {
        ClearChildren(containerToClear);
    }
    public void ClearChildren(GameObject target)
    {
        foreach (Transform child in target.transform)
        {
            Destroy(child.gameObject);
        }
        if (labelToClear != null)
        {
            labelToClear.text = string.Empty;
        }
    }

}
