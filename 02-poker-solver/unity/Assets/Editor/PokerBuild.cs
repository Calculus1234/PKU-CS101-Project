using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 一键打包：菜单 Poker/Build Android APK、Poker/Build PC。
/// 打包参数（公司名/包名/图标/窗口化）集中在 ApplyCommon 里，避免漏配。
/// </summary>
public static class PokerBuild
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    private static void ApplyCommon()
    {
        PlayerSettings.companyName = "LinZhongXiKe";
        PlayerSettings.productName = "残局解牌器";
        PlayerSettings.bundleVersion = "1.2.0";

        // PC 窗口化 1600x900（不要全屏）
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;

        // Android：IL2CPP + ARM64 + Release
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Low);
        PlayerSettings.applicationIdentifier = "com.LinZhongXiKe.PokerSolver";
    }

    [MenuItem("Poker/Build Android APK")]
    public static void BuildAndroid()
    {
        ApplyCommon();
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Build/Android");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, "PokerSolver.apk");

        Debug.Log("[PokerBuild] 开始 Android 构建（IL2CPP/ARM64/Release），首次会很慢…");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });

        var s = report.summary;
        Debug.Log("[PokerBuild] Android 完成 result=" + s.result
            + " size=" + (s.totalSize / (1024 * 1024)) + "MB"
            + " errors=" + s.totalErrors
            + " time=" + s.totalTime);
    }

    [MenuItem("Poker/Build PC")]
    public static void BuildPC()
    {
        ApplyCommon();
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Build/PC");
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, "PokerSolver.exe");

        Debug.Log("[PokerBuild] 开始 PC 构建…");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });

        var s = report.summary;
        Debug.Log("[PokerBuild] PC 完成 result=" + s.result
            + " size=" + (s.totalSize / (1024 * 1024)) + "MB"
            + " errors=" + s.totalErrors
            + " time=" + s.totalTime);
    }

    [MenuItem("Poker/Build Both")]
    public static void BuildBoth()
    {
        BuildPC();
        BuildAndroid();
    }
}
