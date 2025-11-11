using UnityEngine;
using System.Runtime.InteropServices;
using System.Diagnostics;

public class MinimizeCloseScript : MonoBehaviour
{ 
    public MasterNetworkHandler masterNetworkHandler;
    
    // Windows-specific imports
    [DllImport("user32.dll")]
    private static extern int ShowWindow(System.IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern System.IntPtr GetActiveWindow();
    private const int SW_MINIMIZE = 6;

    public void MinimizeWindow()
    {
        #if UNITY_STANDALONE_WIN
            // Windows implementation
            ShowWindow(GetActiveWindow(), SW_MINIMIZE);
        #elif UNITY_STANDALONE_LINUX
            // Linux implementation using wmctrl
            MinimizeWindowLinux();
        #else
            // Fallback for other platforms
            Debug.Log("Minimize not supported on this platform");
        #endif
    }
    
    private void MinimizeWindowLinux()
    {
        try
        {
            // Use wmctrl to minimize the Unity window
            // This requires wmctrl to be installed: sudo apt-get install wmctrl
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "wmctrl",
                Arguments = "-r :ACTIVE: -b add,hidden",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using (Process process = Process.Start(startInfo))
            {
                if (process != null)
                {
                    process.WaitForExit(1000); // Wait max 1 second
                    if (process.ExitCode == 0)
                    {
                        UnityEngine.Debug.Log("Window minimized successfully using wmctrl");
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("wmctrl failed - trying alternative method");
                        MinimizeWindowLinuxAlternative();
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogWarning($"Linux minimize failed: {e.Message} - trying alternative method");
            MinimizeWindowLinuxAlternative();
        }
    }
    
    private void MinimizeWindowLinuxAlternative()
    {
        try
        {
            // Alternative: Use xdotool to minimize
            ProcessStartInfo startInfo = new ProcessStartInfo()
            {
                FileName = "xdotool",
                Arguments = "windowminimize $(xdotool getactivewindow)",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            using (Process process = Process.Start(startInfo))
            {
                if (process != null)
                {
                    process.WaitForExit(1000);
                    if (process.ExitCode == 0)
                    {
                        UnityEngine.Debug.Log("Window minimized successfully using xdotool");
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("Both wmctrl and xdotool failed - minimize not available");
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogWarning($"Alternative Linux minimize also failed: {e.Message}");
        }
    }

    public void CloseApplication()
    {
        masterNetworkHandler?.Pause_Resume();
        Application.Quit();
    }
}
