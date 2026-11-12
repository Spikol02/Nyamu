// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

// Stand-in for the official launcher, its browser and the game, for tests under Wine. What it
// does depends on its file name and on STUB_* environment variables. It is not the real thing
// and holds nothing of it; the "payload" it can drop is four header bytes and padding.

#include <windows.h>
#include <stdio.h>
#include <string.h>

static int ignore_close;

static LRESULT CALLBACK proc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    if (m == WM_CLOSE && ignore_close) return 0;
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcA(w, m, wp, lp);
}

static int env_int(const char* name, int fallback) {
    char buf[32];
    return GetEnvironmentVariableA(name, buf, sizeof buf) ? atoi(buf) : fallback;
}

static void spawn(const char* path) {
    STARTUPINFOA si = {sizeof si};
    PROCESS_INFORMATION pi;
    char cmd[MAX_PATH * 2];
    snprintf(cmd, sizeof cmd, "\"%s\"", path);
    if (CreateProcessA(path, cmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi)) {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
}

int WINAPI WinMain(HINSTANCE inst, HINSTANCE prev, LPSTR cmd, int show) {
    char self[MAX_PATH], dir[MAX_PATH], path[MAX_PATH * 2];
    GetModuleFileNameA(NULL, self, sizeof self);
    strcpy(dir, self);
    *strrchr(dir, '\\') = 0;
    const char* name = strrchr(self, '\\') + 1;
    int launcher = !_strnicmp(name, "nikke_launcher", 14);
    int game_after = launcher ? env_int("STUB_GAME_AFTER", -1) : -1;
    int drop_after = launcher ? env_int("STUB_DROP_AFTER", -1) : -1;
    ignore_close = launcher && env_int("STUB_IGNORE_CLOSE", 0);

    WNDCLASSA wc = {0};
    wc.lpfnWndProc = proc;
    wc.hInstance = inst;
    wc.lpszClassName = "NyakkeStub";
    RegisterClassA(&wc);
    HWND w = CreateWindowA("NyakkeStub", name, WS_OVERLAPPEDWINDOW | WS_VISIBLE, 50, 50, 320, 120, NULL, NULL, inst, NULL);
    if (launcher) {
        snprintf(path, sizeof path, "%s\\tbs_browser.exe", dir);
        spawn(path);
    }
    SetTimer(w, 1, 250, NULL);
    DWORD start = GetTickCount();
    MSG msg;
    while (GetMessageA(&msg, NULL, 0, 0) > 0) {
        if (msg.message == WM_TIMER) {
            DWORD s = (GetTickCount() - start) / 1000;
            if (game_after >= 0 && (int)s >= game_after) {
                game_after = -1;
                snprintf(path, sizeof path, "%s\\..\\NIKKE\\game\\nikke.exe", dir);
                spawn(path);
            }
            if (drop_after >= 0 && (int)s >= drop_after) {
                drop_after = -1;
                char tmp[MAX_PATH * 2];
                snprintf(tmp, sizeof tmp, "%s\\log\\tinyget\\tinyget.stub.log.cdt", dir);
                snprintf(path, sizeof path, "%s\\log\\tinyget\\tinyget.stub.log", dir);
                FILE* f = fopen(tmp, "wb");
                if (f) {
                    fwrite("\x1bLJ\x01", 1, 4, f);
                    for (int i = 0; i < 2000; i++) fputc(0, f);
                    fclose(f);
                    MoveFileA(tmp, path);
                }
            }
            if (s > 120) break;
        }
        DispatchMessageA(&msg);
    }
    return 0;
}
