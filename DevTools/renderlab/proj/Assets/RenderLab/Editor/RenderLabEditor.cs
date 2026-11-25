// Render lab entry point (throwaway project only): -executeMethod RenderLabEditor.Go
// Environment: RL_SITE (dover | szczecin), RL_OUT (output prefix, no extension), RL_VIEWS (optional comma list),
// RL_FOV (vertical degrees, default 60), RL_TIMEOUT (seconds, default 1500), RL_NOPOST=1 (also a _nopost.png per view).
// Opens the world's real scene, keeps only the map viewer, the scene's sun and its Global Volume, points the viewer
// at the site's centre with _loadOnStart on, and enters play mode (no domain or scene reload, so the in-memory edits
// hold and the scene file on disk is never saved). RenderLabRunner does the rest; the editor exits with its code.
using System;
using System.Collections.Generic;
using System.Globalization;
using SomniumSpace.Worlds.SomniumEuroTruck;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RenderLabEditor
{
    static double _deadline;

    static string Env(string name, string fallback)
    {
        string v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    public static void Go()
    {
        try
        {
            Setup();
        }
        catch (Exception e)
        {
            Debug.LogError("[RenderLab] setup failed: " + e);
            EditorApplication.Exit(2);
        }
    }

    static void Setup()
    {
        CultureInfo i = CultureInfo.InvariantCulture;
        RenderLabRunner.Site = Env("RL_SITE", "dover");
        RenderLabRunner.OutPrefix = Env("RL_OUT", "C:/Temp/rl");
        RenderLabRunner.OnlyViews = Env("RL_VIEWS", "");
        RenderLabRunner.Fov = float.Parse(Env("RL_FOV", "60"), i);
        RenderLabRunner.TimeoutSeconds = float.Parse(Env("RL_TIMEOUT", "1500"), i);
        RenderLabRunner.NoPost = Env("RL_NOPOST", "0") == "1";
        RenderLabRunner.Done = false;

        // Shaders must be compiled before they draw: no cyan placeholders in a capture.
        ShaderUtil.allowAsyncCompilation = false;
        EditorSettings.asyncShaderCompilation = false;

        Scene scene = EditorSceneManager.OpenScene("Assets/Euro Truck Scene.unity", OpenSceneMode.Single);
        Debug.Log("[RenderLab] opened " + scene.path + "; fog " + RenderSettings.fog + " " + RenderSettings.fogMode + ", sun " + (RenderSettings.sun != null ? RenderSettings.sun.name : "none"));

        // The sun lives under the Uploader object (Somnium SDK config): lift it to the root so the SDK object can go.
        Light sun = RenderSettings.sun;

        if (sun != null && sun.transform.parent != null)
        {
            sun.transform.SetParent(null, true);
        }

        var keep = new HashSet<string> { "Ets Map Viewer", "Global Volume" };

        if (sun != null)
        {
            keep.Add(sun.gameObject.name);
        }

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (!keep.Contains(root.name))
            {
                root.SetActive(false);
                Debug.Log("[RenderLab] switched off root '" + root.name + "'");
            }
            else
            {
                Debug.Log("[RenderLab] kept root '" + root.name + "'");
            }
        }

        EtsMapViewer viewer = UnityEngine.Object.FindFirstObjectByType<EtsMapViewer>();

        if (viewer == null)
        {
            throw new Exception("no EtsMapViewer in the scene");
        }

        var focus = new GameObject("RL Focus");
        SceneManager.MoveGameObjectToScene(focus, scene);
        var so = new SerializedObject(viewer);
        so.FindProperty("_centerGame").vector3Value = RenderLabRunner.CentreFor(RenderLabRunner.Site);
        so.FindProperty("_loadOnStart").boolValue = true;
        so.FindProperty("_createCameraIfNone").boolValue = false;
        so.FindProperty("_follow").objectReferenceValue = focus.transform;
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log("[RenderLab] map viewer centre " + RenderLabRunner.CentreFor(RenderLabRunner.Site).ToString("0.0") + ", load on start, follow RL Focus");

        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        _deadline = EditorApplication.timeSinceStartup + RenderLabRunner.TimeoutSeconds + 300;
        EditorApplication.playModeStateChanged += state =>
        {
            Debug.Log("[RenderLab] play mode " + state);

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                new GameObject("RL Runner").AddComponent<RenderLabRunner>();
            }
        };
        EditorApplication.update += Poll;
        EditorApplication.EnterPlaymode();
    }

    static void Poll()
    {
        if (RenderLabRunner.Done || EditorApplication.timeSinceStartup > _deadline)
        {
            EditorApplication.update -= Poll;
            int code = RenderLabRunner.Done ? RenderLabRunner.ExitCode : 6;
            Debug.Log("[RenderLab] exiting with " + code);
            EditorApplication.Exit(code);
        }
    }
}
