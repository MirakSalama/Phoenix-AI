namespace NpuChat;

internal static class PortableRuntime
{
    internal static string ModelRoot(string project) =>
        File.Exists(Path.Combine(project, "phoenix-compact.release"))
            ? Path.GetFullPath(Path.Combine(project, "..", "Models"))
            : Path.Combine(project, "cache");

    internal static void Configure(string project)
    {
        string models = ModelRoot(project);
        Set("PHOENIX_MODEL_DIR", models);
        if (!File.Exists(Path.Combine(project, "phoenix-portable.release"))) return;

        string runtime = Path.Combine(project, "runtime");
        string python = Path.Combine(runtime, "python", "python.exe");
        string programs = Path.Combine(project, "cache", "phoenix-portable-programs");
        string driver = Path.Combine(Environment.SystemDirectory, "xrt_coreutil.dll");
        Set("NPU_CHAT_GPU_PYTHON", python);
        Set("NPU_CHAT_PYTHON", Directory.Exists(programs) && File.Exists(driver) ? python : null);
        Set("PHOENIX_NPU_PROGRAMS", programs);
        Set("NPU_CHAT_X8_MODEL", "lfm25-230m");
        Set("PHOENIX_VOICE_PYTHON", Path.Combine(runtime, "voice-python", "python.exe"));
        Set("NPU_DICTATE_BUNDLE", Path.Combine(models, "parakeet"));
        Set("PHOENIX_CPU_DICTATE", Path.Combine(models, "parakeet", "encoder-model.int8.onnx"));
        Set("PHOENIX_VOICE_SETTINGS", Path.Combine(LocalWorkspace.Folder, "voice-settings.json"));
        Set("PYTHONHOME", null);
        Set("PYTHONPATH", null);
        Set("PYTHONNOUSERSITE", "1");
        Set("PYTHONDONTWRITEBYTECODE", "1");
    }

    private static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);
}
