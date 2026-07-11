/**
 * LunchHelper - UIAccess Helper DLL
 *
 * Features:
 *   1. UIAccess token theft via winlogon.exe (killtimer0/uiaccess method)
 *   2. CreateWindowInBand for true UIACCESS-band lock screen windows
 *   3. Low-level keyboard hook to block system shortcuts
 *   4. Process protection against termination
 *   5. Full lock screen UI with countdown, emergency unlock, keypad
 *
 * References:
 *   - killtimer0/uiaccess (https://github.com/killtimer0/uiaccess)
 *   - ADeltaX Blog (https://blog.adeltax.com/window-z-order-in-windows-10/)
 *
 * Build (MSVC):
 *   cl /LD /O2 /MT uiaccess_helper.c /Fe:uiaccess_helper.dll /link user32.lib advapi32.lib shell32.lib comctl32.lib gdi32.lib
 * Build (MinGW):
 *   gcc -shared -O2 -static -o uiaccess_helper.dll uiaccess_helper.c -luser32 -ladvapi32 -lshell32 -lcomctl32 -lgdi32
 */

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <TlHelp32.h>
#include <aclapi.h>
#include <commctrl.h>

#pragma comment(lib, "comctl32.lib")

#ifndef PROTECTED_DACL_SECURITY_INFORMATION
#define PROTECTED_DACL_SECURITY_INFORMATION 0x80000000
#endif

/* ========================================================================
 * Constants
 * ======================================================================== */

#define ZBID_UIACCESS       1
#define IDT_COUNTDOWN       1
#define IDC_UNLOCK_BTN      100
#define IDC_RETURN_BTN      101
#define IDC_KEYPAD_BASE     200
#define UNLOCK_COOLDOWN     5

/* ========================================================================
 * Types
 * ======================================================================== */

typedef HWND (WINAPI *PFN_CreateWindowInBand)(
    DWORD dwExStyle, LPCWSTR lpClassName, LPCWSTR lpWindowName,
    DWORD dwStyle, int X, int Y, int nWidth, int nHeight,
    HWND hWndParent, HMENU hMenu, HINSTANCE hInstance,
    LPVOID lpParam, DWORD dwBand
);

typedef struct {
    HWND hWnd;
    int width, height;

    /* Child controls */
    HWND hTimeLabel;
    HWND hSloganLabel;
    HWND hSloganFrame;
    HWND hCountdownLabel;
    HWND hUnlockBtn;

    HWND hUnlockPanel;
    HWND hPromptLabel;
    HWND hPasswordDisplay;
    HWND hKeypadBtns[12];
    HWND hReturnBtn;

    HWND hBottomSpacer;
} LOCK_WINDOW;

/* ========================================================================
 * Global state
 * ======================================================================== */

static HHOOK         g_hKeyboardHook          = NULL;
static BOOL          g_bDebug                 = FALSE;
static HINSTANCE     g_hInst                  = NULL;
static PFN_CreateWindowInBand g_pCWB         = NULL;
static BOOL          g_hasUIAccess            = FALSE;

/* Lock screen shared state */
static LOCK_WINDOW*  g_windows                = NULL;
static int           g_windowCount            = 0;
static int           g_remaining              = 0;
static WCHAR         g_password[7]            = {0};
static WCHAR         g_slogan[256]            = {0};
static BOOL          g_unlockMode             = FALSE;
static WCHAR         g_input[7]               = {0};
static int           g_inputLen               = 0;
static int           g_cooldownRemaining      = 0;
static BOOL          g_cooldownActive         = FALSE;
static BOOL          g_shouldExit             = FALSE;
static DWORD         g_exitCode               = 0;

static const WCHAR*  WC_LOCK_SCREEN           = L"LunchHelperLS";

/* ========================================================================
 * Debug output
 * ======================================================================== */

static void DebugPrint(const char* fmt, ...) {
    if (!g_bDebug) return;
    va_list args;
    va_start(args, fmt);
    char buf[1024];
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    OutputDebugStringA(buf);
}

static void DebugPrintW(const char* prefix, LPCWSTR wstr) {
    if (!g_bDebug) return;
    char buf[1024];
    int prefixLen = lstrlenA(prefix);
    if (prefixLen > 0) {
        if (prefixLen >= (int)sizeof(buf)) prefixLen = sizeof(buf) - 1;
        memcpy(buf, prefix, prefixLen);
    }
    int converted = WideCharToMultiByte(CP_UTF8, 0, wstr, -1,
                                        buf + prefixLen,
                                        (int)sizeof(buf) - prefixLen,
                                        NULL, NULL);
    if (converted > 0) {
        OutputDebugStringA(buf);
    }
}

/* ========================================================================
 * Process protection
 * ======================================================================== */

static BOOL ProtectProcess(void) {
    HANDLE hProcess = GetCurrentProcess();
    PSECURITY_DESCRIPTOR pSD = NULL;
    PACL pDACL = NULL;
    DWORD dwErr;

    dwErr = GetSecurityInfo(hProcess, SE_KERNEL_OBJECT,
                            DACL_SECURITY_INFORMATION,
                            NULL, NULL, &pDACL, NULL, &pSD);
    if (dwErr != ERROR_SUCCESS) {
        DebugPrint("GetSecurityInfo failed: %lu\n", dwErr);
        return FALSE;
    }

    SID_IDENTIFIER_AUTHORITY worldAuth = SECURITY_WORLD_SID_AUTHORITY;
    PSID pEveryoneSid = NULL;
    if (!AllocateAndInitializeSid(&worldAuth, 1, SECURITY_WORLD_RID,
                                  0, 0, 0, 0, 0, 0, 0, &pEveryoneSid)) {
        LocalFree(pSD);
        return FALSE;
    }

    EXPLICIT_ACCESSW ea = {0};
    ea.grfAccessPermissions = PROCESS_TERMINATE;
    ea.grfAccessMode = DENY_ACCESS;
    ea.grfInheritance = NO_INHERITANCE;
    ea.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    ea.Trustee.TrusteeType = TRUSTEE_IS_WELL_KNOWN_GROUP;

#pragma warning(push)
#pragma warning(disable:4133)
    ea.Trustee.ptstrName = (LPWSTR)pEveryoneSid;
#pragma warning(pop)

    PACL pNewDACL = NULL;
    dwErr = SetEntriesInAclW(1, &ea, pDACL, &pNewDACL);
    FreeSid(pEveryoneSid);

    if (dwErr != ERROR_SUCCESS) {
        DebugPrint("SetEntriesInAcl failed: %lu\n", dwErr);
        LocalFree(pSD);
        return FALSE;
    }

    dwErr = SetSecurityInfo(hProcess, SE_KERNEL_OBJECT,
                            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                            NULL, NULL, pNewDACL, NULL);

    LocalFree(pNewDACL);
    LocalFree(pSD);

    if (dwErr == ERROR_SUCCESS) {
        DebugPrint("Process protection enabled\n");
    }
    return (dwErr == ERROR_SUCCESS);
}

/* ========================================================================
 * Keyboard hook
 * ======================================================================== */

LRESULT CALLBACK LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION) {
        KBDLLHOOKSTRUCT *pKb = (KBDLLHOOKSTRUCT *)lParam;

        if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) {
            DWORD vkCode = pKb->vkCode;

            if (vkCode == VK_LWIN || vkCode == VK_RWIN) {
                DebugPrint("Blocked: Win key\n");
                return 1;
            }

            BOOL bAlt   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
            BOOL bCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            BOOL bShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;

            if (bAlt && vkCode == VK_TAB)    { DebugPrint("Blocked: Alt+Tab\n"); return 1; }
            if (bAlt && vkCode == VK_F4)     { DebugPrint("Blocked: Alt+F4\n"); return 1; }
            if (bAlt && vkCode == VK_ESCAPE) { DebugPrint("Blocked: Alt+Esc\n"); return 1; }
            if (bAlt && vkCode == VK_SPACE)  { DebugPrint("Blocked: Alt+Space\n"); return 1; }
            if (bCtrl && vkCode == VK_ESCAPE && !bShift) { DebugPrint("Blocked: Ctrl+Esc\n"); return 1; }
            if (bCtrl && bShift && vkCode == VK_ESCAPE)  { DebugPrint("Blocked: Ctrl+Shift+Esc\n"); return 1; }

            if ((GetAsyncKeyState(VK_LWIN) & 0x8000) || (GetAsyncKeyState(VK_RWIN) & 0x8000)) {
                if (vkCode == 'D' || vkCode == 'M' || vkCode == 'R' || vkCode == 'L')
                    { DebugPrint("Blocked: Win+key\n"); return 1; }
            }
            if (bCtrl && bAlt && (vkCode >= VK_LEFT && vkCode <= VK_DOWN))
                { DebugPrint("Blocked: Ctrl+Alt+Arrow\n"); return 1; }
        }
    }
    return CallNextHookEx(NULL, nCode, wParam, lParam);
}

__declspec(dllexport) BOOL WINAPI InstallKeyboardHook(void) {
    if (g_hKeyboardHook) return TRUE;
    g_hKeyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc,
                                        g_hInst, 0);
    if (!g_hKeyboardHook) {
        DebugPrint("InstallKeyboardHook failed: %lu\n", GetLastError());
        return FALSE;
    }
    DebugPrint("Keyboard hook installed\n");
    return TRUE;
}

__declspec(dllexport) void WINAPI RemoveKeyboardHook(void) {
    if (g_hKeyboardHook) {
        UnhookWindowsHookEx(g_hKeyboardHook);
        g_hKeyboardHook = NULL;
        DebugPrint("Keyboard hook removed\n");
    }
}

__declspec(dllexport) void WINAPI SetDebugMode(BOOL debug) {
    g_bDebug = debug;
}

/* ========================================================================
 * UIAccess helpers
 * ======================================================================== */

static BOOL EnablePrivilege(LPCWSTR privilegeName) {
    HANDLE hToken;
    TOKEN_PRIVILEGES tp;
    LUID luid;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &hToken))
        return FALSE;
    if (!LookupPrivilegeValueW(NULL, privilegeName, &luid)) { CloseHandle(hToken); return FALSE; }
    tp.PrivilegeCount = 1;
    tp.Privileges[0].Luid = luid;
    tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    BOOL result = AdjustTokenPrivileges(hToken, FALSE, &tp, sizeof(TOKEN_PRIVILEGES), NULL, NULL);
    CloseHandle(hToken);
    return result && GetLastError() == ERROR_SUCCESS;
}

static HANDLE GetWinlogonToken(void) {
    DWORD currentSessionId;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &currentSessionId)) {
        DebugPrint("ProcessIdToSessionId failed: %lu\n", GetLastError());
        return NULL;
    }
    HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnapshot == INVALID_HANDLE_VALUE) return NULL;

    PROCESSENTRY32W pe;
    pe.dwSize = sizeof(pe);
    HANDLE hToken = NULL;

    if (Process32FirstW(hSnapshot, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"winlogon.exe") == 0) {
                DWORD winlogonSessionId;
                if (ProcessIdToSessionId(pe.th32ProcessID, &winlogonSessionId) &&
                    winlogonSessionId == currentSessionId) {
                    HANDLE hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, pe.th32ProcessID);
                    if (hProcess) {
                        if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, &hToken))
                            DebugPrint("OpenProcessToken(winlogon) failed: %lu\n", GetLastError());
                        CloseHandle(hProcess);
                    }
                    break;
                }
            }
        } while (Process32NextW(hSnapshot, &pe));
    }
    CloseHandle(hSnapshot);
    return hToken;
}

static BOOL HasUIAccess(void) {
    HANDLE hToken;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) return FALSE;
    DWORD uiAccess = 0;
    DWORD size = 0;
    BOOL ok = GetTokenInformation(hToken, TokenUIAccess, &uiAccess, sizeof(uiAccess), &size);
    CloseHandle(hToken);
    return ok && uiAccess != 0;
}

__declspec(dllexport) DWORD WINAPI PrepareForUIAccess(void) {
    DebugPrint("=== PrepareForUIAccess ===\n");
    if (HasUIAccess()) {
        DebugPrint("Already have UIAccess\n");
        g_hasUIAccess = TRUE;
        return ERROR_SUCCESS;
    }
    if (!IsUserAnAdmin()) {
        DebugPrint("Not running as admin\n");
        return ERROR_ACCESS_DENIED;
    }
    EnablePrivilege(SE_DEBUG_NAME);

    HANDLE hWinlogonToken = GetWinlogonToken();
    if (!hWinlogonToken) return ERROR_NOT_FOUND;

    HANDLE hNewToken = NULL;
    if (!DuplicateTokenEx(hWinlogonToken, MAXIMUM_ALLOWED, NULL,
                          SecurityImpersonation, TokenPrimary, &hNewToken)) {
        DebugPrint("DuplicateTokenEx failed: %lu\n", GetLastError());
        CloseHandle(hWinlogonToken);
        return GetLastError();
    }
    CloseHandle(hWinlogonToken);

    DWORD uiAccessFlag = 1;
    if (!SetTokenInformation(hNewToken, TokenUIAccess, &uiAccessFlag, sizeof(uiAccessFlag))) {
        DebugPrint("SetTokenInformation(TokenUIAccess) failed: %lu\n", GetLastError());
        CloseHandle(hNewToken);
        return GetLastError();
    }
    DebugPrint("TokenUIAccess set successfully\n");

    LPWSTR cmdLine = GetCommandLineW();
    DebugPrintW("Command line: ", cmdLine);

    STARTUPINFOW si = {0};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {0};

    if (!CreateProcessWithTokenW(hNewToken, 0, NULL, cmdLine, 0, NULL, NULL, &si, &pi)) {
        DebugPrint("CreateProcessWithTokenW failed: %lu\n", GetLastError());
        CloseHandle(hNewToken);
        return GetLastError();
    }
    DebugPrint("New process created with UIAccess, PID: %lu\n", pi.dwProcessId);

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    CloseHandle(hNewToken);
    ExitProcess(0);
    return ERROR_SUCCESS;
}

__declspec(dllexport) BOOL WINAPI IsElevated(void) { return IsUserAnAdmin(); }
__declspec(dllexport) BOOL WINAPI GetUIAccessStatus(void) { return HasUIAccess(); }
__declspec(dllexport) BOOL WINAPI IsSystemProcess(void) {
    HANDLE hToken;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken)) return FALSE;
    BYTE sidBuffer[SECURITY_MAX_SID_SIZE];
    DWORD sidSize = sizeof(sidBuffer);
    PTOKEN_USER pTokenUser = (PTOKEN_USER)sidBuffer;
    if (!GetTokenInformation(hToken, TokenUser, pTokenUser, sidSize, &sidSize))
        { CloseHandle(hToken); return FALSE; }
    CloseHandle(hToken);
    PSID systemSid = NULL;
    SID_IDENTIFIER_AUTHORITY ntAuthority = SECURITY_NT_AUTHORITY;
    if (AllocateAndInitializeSid(&ntAuthority, 1, SECURITY_LOCAL_SYSTEM_RID,
                                  0, 0, 0, 0, 0, 0, 0, &systemSid)) {
        BOOL isSystem = EqualSid(pTokenUser->User.Sid, systemSid);
        FreeSid(systemSid);
        return isSystem;
    }
    return FALSE;
}

/* ========================================================================
 * CreateWindowInBand dynamic loading
 * ======================================================================== */

static BOOL InitCreateWindowInBand(void) {
    if (g_pCWB) return TRUE;
    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (!hUser32) return FALSE;
    g_pCWB = (PFN_CreateWindowInBand)GetProcAddress(hUser32, MAKEINTRESOURCEA(2488));
    if (g_pCWB) {
        DebugPrint("CreateWindowInBand loaded (ordinal 2488)\n");
    } else {
        DebugPrint("CreateWindowInBand not available, using CreateWindowEx fallback\n");
    }
    return g_pCWB != NULL;
}

static HWND CreateWindowBand(int x, int y, int w, int h, DWORD band) {
    DWORD exStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
    DWORD style   = WS_POPUP;

    if (g_pCWB) {
        return g_pCWB(exStyle, WC_LOCK_SCREEN, L"LunchHelper", style,
                      x, y, w, h, NULL, NULL, g_hInst, NULL, band);
    }
    return CreateWindowExW(exStyle, WC_LOCK_SCREEN, L"LunchHelper", style,
                           x, y, w, h, NULL, NULL, g_hInst, NULL);
}

/* ========================================================================
 * AttachThreadInput - steal focus from foreground window
 * ======================================================================== */

static void StealFocus(HWND hWnd) {
    HWND hForeground = GetForegroundWindow();
    if (!hForeground) return;
    DWORD dwForeThread = GetWindowThreadProcessId(hForeground, NULL);
    DWORD dwCurThread  = GetCurrentThreadId();
    if (dwForeThread == dwCurThread) return;

    if (!AttachThreadInput(dwCurThread, dwForeThread, TRUE)) {
        DebugPrint("AttachThreadInput(TRUE) failed: %lu\n", GetLastError());
        return;
    }
    SetForegroundWindow(hWnd);
    BringWindowToTop(hWnd);
    if (!AttachThreadInput(dwCurThread, dwForeThread, FALSE)) {
        DebugPrint("AttachThreadInput(FALSE) failed: %lu\n", GetLastError());
    }
    DebugPrint("Focus stolen from foreground window\n");
}

/* ========================================================================
 * Lock screen window creation helpers
 * ======================================================================== */

static HFONT CreateLockFont(int height, BOOL bold) {
    return CreateFontW(
        height, 0, 0, 0,
        bold ? FW_BOLD : FW_NORMAL,
        FALSE, FALSE, FALSE,
        DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,
        CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE,
        L"Microsoft YaHei"
    );
}

#define BTN_W 90
#define BTN_H 55
#define BTN_GAP 8

static void LayoutKeypad(HWND hParent, HWND btns[12], int panelW, int panelH) {
    int gridW = 3 * BTN_W + 2 * BTN_GAP;
    int gridH = 4 * BTN_H + 3 * BTN_GAP;
    int startX = (panelW - gridW) / 2;
    int startY = (panelH - gridH) / 2 + 40;

    const WCHAR* labels[] = { L"1",L"2",L"3", L"4",L"5",L"6", L"7",L"8",L"9", L"\x232B",L"0",L"" };
    for (int r = 0; r < 4; r++) {
        for (int c = 0; c < 3; c++) {
            int idx = r * 3 + c;
            int x = startX + c * (BTN_W + BTN_GAP);
            int y = startY + r * (BTN_H + BTN_GAP);
            HWND btn = CreateWindowExW(0, L"BUTTON", labels[idx],
                                       WS_CHILD | BS_PUSHBUTTON,
                                       x, y, BTN_W, BTN_H,
                                       hParent, (HMENU)(INT_PTR)(IDC_KEYPAD_BASE + idx),
                                       g_hInst, NULL);
            if (btn) {
                HFONT f = CreateLockFont(18, TRUE);
                SendMessageW(btn, WM_SETFONT, (WPARAM)f, TRUE);
                if (labels[idx][0] == L'\0') EnableWindow(btn, FALSE);
            }
            btns[idx] = btn;
        }
    }
}

static LOCK_WINDOW* CreateLockWindow(int x, int y, int w, int h) {
    LOCK_WINDOW* lw = (LOCK_WINDOW*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(LOCK_WINDOW));
    if (!lw) return NULL;
    lw->width = w; lw->height = h;

    DWORD band = g_hasUIAccess ? ZBID_UIACCESS : 0;
    HWND hWnd = CreateWindowBand(x, y, w, h, band);
    if (!hWnd) { HeapFree(GetProcessHeap(), 0, lw); return NULL; }
    lw->hWnd = hWnd;

    /* Time label at top */
    HFONT fTime = CreateLockFont(20, FALSE);
    lw->hTimeLabel = CreateWindowExW(0, L"STATIC", L"",
                                     WS_CHILD | WS_VISIBLE | SS_CENTER,
                                     0, 15, w, 35,
                                     hWnd, NULL, g_hInst, NULL);
    SendMessageW(lw->hTimeLabel, WM_SETFONT, (WPARAM)fTime, TRUE);

    /* Slogan frame */
    lw->hSloganFrame = CreateWindowExW(0, L"STATIC", L"",
                                       WS_CHILD | WS_VISIBLE | SS_CENTER,
                                       50, h/2 - 100, w - 100, 80,
                                       hWnd, NULL, g_hInst, NULL);
    HFONT fSlogan = CreateLockFont(34, TRUE);
    lw->hSloganLabel = CreateWindowExW(0, L"STATIC", g_slogan,
                                       WS_CHILD | WS_VISIBLE | SS_CENTER,
                                       0, 0, w - 100, 80,
                                       lw->hSloganFrame, NULL, g_hInst, NULL);
    SendMessageW(lw->hSloganLabel, WM_SETFONT, (WPARAM)fSlogan, TRUE);

    /* Countdown */
    HFONT fCd = CreateLockFont(22, FALSE);
    lw->hCountdownLabel = CreateWindowExW(0, L"STATIC", L"",
                                          WS_CHILD | WS_VISIBLE | SS_CENTER,
                                          w/2 - 300, h/2 + 20, 600, 40,
                                          hWnd, NULL, g_hInst, NULL);
    SendMessageW(lw->hCountdownLabel, WM_SETFONT, (WPARAM)fCd, TRUE);

    /* Emergency unlock button */
    HFONT fBtn = CreateLockFont(16, TRUE);
    lw->hUnlockBtn = CreateWindowExW(0, L"BUTTON", L"Emergency Unlock",
                                     WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                                     w/2 - 90, h/2 + 80, 180, 50,
                                     hWnd, (HMENU)(INT_PTR)IDC_UNLOCK_BTN, g_hInst, NULL);
    SendMessageW(lw->hUnlockBtn, WM_SETFONT, (WPARAM)fBtn, TRUE);

    /* Unlock panel (initially hidden) */
    lw->hUnlockPanel = CreateWindowExW(0, L"STATIC", L"",
                                       WS_CHILD | SS_CENTER,
                                       0, 0, w, h,
                                       hWnd, NULL, g_hInst, NULL);

    /* Prompt label */
    HFONT fPrompt = CreateLockFont(16, FALSE);
    lw->hPromptLabel = CreateWindowExW(0, L"STATIC", L"Enter emergency unlock password",
                                       WS_CHILD | WS_VISIBLE | SS_CENTER,
                                       w/2 - 350, 80, 700, 35,
                                       lw->hUnlockPanel, NULL, g_hInst, NULL);
    SendMessageW(lw->hPromptLabel, WM_SETFONT, (WPARAM)fPrompt, TRUE);

    /* Password display */
    HFONT fPwd = CreateLockFont(28, TRUE);
    lw->hPasswordDisplay = CreateWindowExW(WS_EX_CLIENTEDGE, L"STATIC", L"",
                                           WS_CHILD | WS_VISIBLE | SS_CENTER,
                                           w/2 - 120, 130, 240, 55,
                                           lw->hUnlockPanel, NULL, g_hInst, NULL);
    SendMessageW(lw->hPasswordDisplay, WM_SETFONT, (WPARAM)fPwd, TRUE);

    /* Keypad — parented to hWnd so BN_CLICKED reaches LockScreenWndProc */
    LayoutKeypad(hWnd, lw->hKeypadBtns, w, h);

    /* Return button — parented to hWnd for same reason */
    HFONT fRet = CreateLockFont(14, FALSE);
    lw->hReturnBtn = CreateWindowExW(0, L"BUTTON", L"Back",
                                     WS_CHILD | BS_PUSHBUTTON,
                                     w/2 - 70, h - 120, 140, 45,
                                     hWnd, (HMENU)(INT_PTR)IDC_RETURN_BTN, g_hInst, NULL);
    SendMessageW(lw->hReturnBtn, WM_SETFONT, (WPARAM)fRet, TRUE);

    ShowWindow(lw->hUnlockPanel, SW_HIDE);

    /* Bottom spacer */
    lw->hBottomSpacer = CreateWindowExW(0, L"STATIC", L"",
                                        WS_CHILD, 0, 0, 1, 1,
                                        hWnd, NULL, g_hInst, NULL);

    ShowWindow(hWnd, SW_SHOW);
    UpdateWindow(hWnd);

    return lw;
}

static void DestroyLockWindow(LOCK_WINDOW* lw) {
    if (!lw) return;
    if (lw->hWnd) DestroyWindow(lw->hWnd);
    /* Note: lw points into g_windows array, do NOT HeapFree it here */
}

/* ========================================================================
 * Monitor enumeration
 * ======================================================================== */

typedef struct {
    RECT* rects;
    int count;
    int capacity;
} MONITOR_LIST;

static BOOL CALLBACK MonitorEnumProc(HMONITOR hMon, HDC hdc, LPRECT pRect, LPARAM lParam) {
    (void)hMon; (void)hdc;
    MONITOR_LIST* list = (MONITOR_LIST*)lParam;
    if (list->count >= list->capacity) {
        int newCap = list->capacity * 2;
        if (newCap < 4) newCap = 4;
        RECT* newRects = (RECT*)HeapAlloc(GetProcessHeap(), 0, newCap * sizeof(RECT));
        if (!newRects) return FALSE;
        if (list->rects) {
            memcpy(newRects, list->rects, list->count * sizeof(RECT));
            HeapFree(GetProcessHeap(), 0, list->rects);
        }
        list->rects = newRects;
        list->capacity = newCap;
    }
    list->rects[list->count++] = *pRect;
    return TRUE;
}

/* ========================================================================
 * UI update helpers
 * ======================================================================== */

static void UpdateTimeDisplay(LOCK_WINDOW* lw) {
    SYSTEMTIME st;
    GetLocalTime(&st);
    WCHAR buf[64];
    wsprintfW(buf, L"%04d-%02d-%02d %02d:%02d:%02d",
              st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    SetWindowTextW(lw->hTimeLabel, buf);
}

static void UpdateCountdownDisplay(LOCK_WINDOW* lw) {
    WCHAR buf[64];
    wsprintfW(buf, L"%d second(s) until auto-unlock", g_remaining);
    if (g_remaining <= 0) wcscpy(buf, L"Unlocking...");
    SetWindowTextW(lw->hCountdownLabel, buf);
}

static void UpdatePasswordDisplay(LOCK_WINDOW* lw) {
    WCHAR buf[7] = {0};
    for (int i = 0; i < g_inputLen; i++) buf[i] = L'*';
    SetWindowTextW(lw->hPasswordDisplay, buf);
}

static void SetKeypadState(BOOL enabled) {
    for (int i = 0; i < g_windowCount; i++) {
        for (int j = 0; j < 12; j++) {
            if (g_windows[i].hKeypadBtns[j])
                EnableWindow(g_windows[i].hKeypadBtns[j], enabled);
        }
    }
}

static void UpdatePromptLabel(const WCHAR* text) {
    for (int i = 0; i < g_windowCount; i++)
        SetWindowTextW(g_windows[i].hPromptLabel, text);
}

static void ShowUnlockMode(BOOL show) {
    g_unlockMode = show;
    for (int i = 0; i < g_windowCount; i++) {
        if (g_windows[i].hSloganFrame) ShowWindow(g_windows[i].hSloganFrame, show ? SW_HIDE : SW_SHOW);
        if (g_windows[i].hUnlockBtn) ShowWindow(g_windows[i].hUnlockBtn, show ? SW_HIDE : SW_SHOW);
        if (g_windows[i].hUnlockPanel) ShowWindow(g_windows[i].hUnlockPanel, show ? SW_SHOW : SW_HIDE);
        /* Keypad buttons and return button are direct children of hWnd, not hUnlockPanel */
        for (int j = 0; j < 12; j++) {
            if (g_windows[i].hKeypadBtns[j])
                ShowWindow(g_windows[i].hKeypadBtns[j], show ? SW_SHOW : SW_HIDE);
        }
        if (g_windows[i].hReturnBtn)
            ShowWindow(g_windows[i].hReturnBtn, show ? SW_SHOW : SW_HIDE);
    }
    if (show && g_windowCount > 0 && g_windows[0].hWnd) {
        g_inputLen = 0;
        UpdatePasswordDisplay(&g_windows[0]);
        UpdatePromptLabel(L"Enter emergency unlock password");
        if (!g_cooldownActive) SetKeypadState(TRUE);
    }
}

/* ========================================================================
 * Password check & cooldown
 * ======================================================================== */

static void DoUnlock(void) {
    DebugPrint("Password correct, unlocking\n");
    g_exitCode = 1;
    g_shouldExit = TRUE;
    PostQuitMessage(0);
}

static void DoCooldown(void) {
    g_cooldownActive = TRUE;
    g_cooldownRemaining = UNLOCK_COOLDOWN;
    SetKeypadState(FALSE);
    WCHAR buf[64];
    wsprintfW(buf, L"Wrong password, retry in %d second(s)", g_cooldownRemaining);
    UpdatePromptLabel(buf);
}

static void CheckPassword(void) {
    if (wcscmp(g_input, g_password) == 0) {
        DoUnlock();
    } else {
        DebugPrint("Wrong password attempt\n");
        g_inputLen = 0;
        if (g_windowCount > 0 && g_windows[0].hWnd) {
            UpdatePasswordDisplay(&g_windows[0]);
        }
        DoCooldown();
    }
}

/* ========================================================================
 * Window procedure
 * ======================================================================== */

static LRESULT CALLBACK LockScreenWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CTLCOLORSTATIC: {
        HDC hdc = (HDC)wParam;
        SetBkColor(hdc, RGB(0, 0, 0));
        SetTextColor(hdc, RGB(255, 255, 255));
        return (LRESULT)GetStockObject(BLACK_BRUSH);
    }
    case WM_CTLCOLORBTN: {
        HDC hdc = (HDC)wParam;
        SetBkColor(hdc, RGB(0, 0, 0));
        return (LRESULT)GetStockObject(BLACK_BRUSH);
    }
    case WM_COMMAND: {
        WORD id = LOWORD(wParam);
        WORD code = HIWORD(wParam);
        if (code != BN_CLICKED) break;

        if (id == IDC_UNLOCK_BTN) {
            ShowUnlockMode(TRUE);
        } else if (id == IDC_RETURN_BTN) {
            ShowUnlockMode(FALSE);
        } else if (id >= IDC_KEYPAD_BASE && id < IDC_KEYPAD_BASE + 12) {
            if (g_cooldownActive) break;
            int idx = id - IDC_KEYPAD_BASE;
            if (idx == 9) { /* backspace */
                if (g_inputLen > 0) g_inputLen--;
                g_input[g_inputLen] = L'\0';
            } else if (idx == 11) {
                /* empty button, ignore */
            } else {
                if (g_inputLen < 6) {
                    if (idx == 10) g_input[g_inputLen++] = L'0';
                    else g_input[g_inputLen++] = L'0' + (WCHAR)(idx + 1);
                    g_input[g_inputLen] = L'\0';
                }
            }
            /* Update all windows */
            for (int i = 0; i < g_windowCount; i++)
                UpdatePasswordDisplay(&g_windows[i]);
            if (g_inputLen == 6) CheckPassword();
        }
        break;
    }
    case WM_TIMER:
        if (wParam == IDT_COUNTDOWN) {
            /* Update time on all windows */
            for (int i = 0; i < g_windowCount; i++)
                UpdateTimeDisplay(&g_windows[i]);

            /* Countdown */
            if (g_remaining > 0) {
                g_remaining--;
                for (int i = 0; i < g_windowCount; i++)
                    UpdateCountdownDisplay(&g_windows[i]);
                if (g_remaining <= 0) {
                    g_exitCode = 0;
                    g_shouldExit = TRUE;
                    PostQuitMessage(0);
                }
            }

            /* Cooldown */
            if (g_cooldownActive) {
                g_cooldownRemaining--;
                if (g_cooldownRemaining > 0) {
                    WCHAR buf[64];
                    wsprintfW(buf, L"Wrong password, retry in %d second(s)", g_cooldownRemaining);
                    UpdatePromptLabel(buf);
                } else {
                    g_cooldownActive = FALSE;
                    SetKeypadState(TRUE);
                    UpdatePromptLabel(L"Enter emergency unlock password");
                    DebugPrint("Cooldown ended\n");
                }
            }
        }
        return 0;

    case WM_DESTROY:
        return 0;
    }
    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

/* ========================================================================
 * Main entry: RunLockScreen (blocking, returns when unlocked)
 * ======================================================================== */

__declspec(dllexport) DWORD WINAPI RunLockScreen(
    int durationSeconds,
    LPCWSTR password,
    LPCWSTR slogan,
    BOOL debug
) {
    g_bDebug = debug;
    g_remaining = durationSeconds;
    g_shouldExit = FALSE;
    g_exitCode = 0;
    g_unlockMode = FALSE;
    g_inputLen = 0;
    g_cooldownActive = FALSE;
    g_cooldownRemaining = 0;
    g_input[0] = L'\0';

    if (password && wcslen(password) == 6)
        wcscpy_s(g_password, 7, password);
    else
        wcscpy_s(g_password, 7, L"000000");

    if (slogan && wcslen(slogan) > 0) {
        size_t sloganLen = wcslen(slogan);
        if (sloganLen >= 256) sloganLen = 255;
        wcsncpy_s(g_slogan, 256, slogan, sloganLen);
        g_slogan[sloganLen] = L'\0';
    } else
        g_slogan[0] = L'\0';

    DebugPrint("=== RunLockScreen ===\n");
    DebugPrint("Duration: %d seconds\n", g_remaining);
    DebugPrint("Password: ******\n");
    DebugPrintW("Slogan: ", g_slogan);

    g_hInst = GetModuleHandleW(L"uiaccess_helper.dll");
    if (!g_hInst) g_hInst = GetModuleHandleW(NULL);

    g_hasUIAccess = HasUIAccess();
    DebugPrint("UIAccess: %s\n", g_hasUIAccess ? "YES" : "NO");

    InitCreateWindowInBand();
    DebugPrint("CreateWindowInBand: %s\n", g_pCWB ? "available" : "unavailable");

    if (g_hasUIAccess) {
        ProtectProcess();
    }

    InstallKeyboardHook();

    /* Register window class */
    WNDCLASSEXW wc = {0};
    wc.cbSize = sizeof(wc);
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = LockScreenWndProc;
    wc.hInstance = g_hInst;
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    wc.lpszClassName = WC_LOCK_SCREEN;
    if (!RegisterClassExW(&wc)) {
        DebugPrint("RegisterClassExW failed: %lu\n", GetLastError());
        RemoveKeyboardHook();
        return (DWORD)-1;
    }

    /* Enumerate monitors */
    MONITOR_LIST monList = {0};
    monList.capacity = 4;
    monList.rects = (RECT*)HeapAlloc(GetProcessHeap(), 0, monList.capacity * sizeof(RECT));
    if (!monList.rects) {
        DebugPrint("HeapAlloc monitor rects failed\n");
        RemoveKeyboardHook();
        UnregisterClassW(WC_LOCK_SCREEN, g_hInst);
        return (DWORD)-1;
    }
    EnumDisplayMonitors(NULL, NULL, MonitorEnumProc, (LPARAM)&monList);
    DebugPrint("Monitors detected: %d\n", monList.count);

    if (monList.count == 0) {
        DebugPrint("No monitors detected, aborting\n");
        HeapFree(GetProcessHeap(), 0, monList.rects);
        RemoveKeyboardHook();
        UnregisterClassW(WC_LOCK_SCREEN, g_hInst);
        return (DWORD)-1;
    }

    /* Create windows */
    g_windowCount = monList.count;
    g_windows = (LOCK_WINDOW*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY,
                                         g_windowCount * sizeof(LOCK_WINDOW));
    if (!g_windows) {
        DebugPrint("HeapAlloc g_windows failed\n");
        HeapFree(GetProcessHeap(), 0, monList.rects);
        RemoveKeyboardHook();
        UnregisterClassW(WC_LOCK_SCREEN, g_hInst);
        return (DWORD)-1;
    }

    for (int i = 0; i < g_windowCount; i++) {
        RECT r = monList.rects[i];
        LOCK_WINDOW* lw = CreateLockWindow(r.left, r.top,
                                           r.right - r.left, r.bottom - r.top);
        if (lw) {
            g_windows[i] = *lw;
            HeapFree(GetProcessHeap(), 0, lw);
            UpdateTimeDisplay(&g_windows[i]);
            UpdateCountdownDisplay(&g_windows[i]);
        } else {
            DebugPrint("CreateLockWindow failed for monitor %d\n", i);
        }
    }
    HeapFree(GetProcessHeap(), 0, monList.rects);

    /* Verify at least one window was created */
    if (g_windowCount == 0 || !g_windows[0].hWnd) {
        DebugPrint("No lock window created, aborting\n");
        for (int i = 0; i < g_windowCount; i++)
            DestroyLockWindow(&g_windows[i]);
        HeapFree(GetProcessHeap(), 0, g_windows);
        g_windows = NULL;
        RemoveKeyboardHook();
        UnregisterClassW(WC_LOCK_SCREEN, g_hInst);
        return (DWORD)-1;
    }

    /* Steal focus */
    if (g_windowCount > 0 && g_windows[0].hWnd) {
        StealFocus(g_windows[0].hWnd);
    }

    /* Start timer */
    SetTimer(g_windows[0].hWnd, IDT_COUNTDOWN, 1000, NULL);

    DebugPrint("Lock screen active, entering message loop\n");

    /* Message loop */
    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
        if (g_shouldExit) break;
    }

    /* Cleanup */
    if (g_windows && g_windowCount > 0 && g_windows[0].hWnd) {
        KillTimer(g_windows[0].hWnd, IDT_COUNTDOWN);
    }
    for (int i = 0; i < g_windowCount; i++)
        DestroyLockWindow(&g_windows[i]);
    if (g_windows) {
        HeapFree(GetProcessHeap(), 0, g_windows);
        g_windows = NULL;
    }
    g_windowCount = 0;

    RemoveKeyboardHook();
    UnregisterClassW(WC_LOCK_SCREEN, g_hInst);

    DebugPrint("Lock screen exited, code: %lu\n", g_exitCode);
    return g_exitCode;
}

/* ========================================================================
 * DLL entry point
 * ======================================================================== */

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    (void)lpReserved;
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
        g_hInst = hModule;
        DisableThreadLibraryCalls(hModule);
        break;
    case DLL_PROCESS_DETACH:
        RemoveKeyboardHook();
        break;
    }
    return TRUE;
}