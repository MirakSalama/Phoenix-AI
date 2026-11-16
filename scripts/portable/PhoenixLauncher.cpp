#include <windows.h>

#include <filesystem>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    wchar_t executable[32768];
    const DWORD length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    if (length == 0 || length >= ARRAYSIZE(executable))
        return 1;

    const auto root = std::filesystem::path(executable).parent_path();
    const auto app = root / L"App";
    const auto program = app / L"PhoenixAI.exe";
    const DWORD models = GetFileAttributesW((root / L"Models").c_str());
    if (GetFileAttributesW(program.c_str()) == INVALID_FILE_ATTRIBUTES ||
        models == INVALID_FILE_ATTRIBUTES || !(models & FILE_ATTRIBUTE_DIRECTORY))
    {
        MessageBoxW(nullptr, L"Extract the full Phoenix AI package and keep App and Models beside Phoenix AI.exe.",
                    L"Phoenix AI", MB_OK | MB_ICONERROR);
        return 1;
    }

    auto command = L"\"" + program.native() + L"\"";
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(program.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, app.c_str(), &startup, &process))
    {
        const auto error = L"Windows could not start Phoenix AI (error " + std::to_wstring(GetLastError()) + L").";
        MessageBoxW(nullptr, error.c_str(), L"Phoenix AI", MB_OK | MB_ICONERROR);
        return 1;
    }

    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
