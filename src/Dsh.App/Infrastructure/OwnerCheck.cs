using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Dsh.App.Infrastructure;

/// <summary>Confirms the person at the keyboard is the signed-in user before a vault secret is shown
/// or copied: Windows Hello (face, fingerprint, PIN) when it is set up, otherwise the Windows
/// password. A PC with no way to check the owner reveals directly, like the macOS app.
///
/// Windows Hello is reached through its COM interop interface directly, so the app doesn't need the
/// Windows SDK projection (and its 25 MB of assemblies) for one call.</summary>
public static class OwnerCheck
{
    public enum Outcome { Verified, Declined, Unavailable }

    /// <summary>Ask. <paramref name="reason"/> finishes "DSH wants to …".</summary>
    public static async Task<(bool Ok, string? Error)> ConfirmAsync(Window owner, string reason)
    {
        if (SelfTest.Current is not null) return (true, null);
        var hwnd = new WindowInteropHelper(owner).Handle;
        switch (await HelloAsync(hwnd, $"DSH wants to {reason}."))
        {
            case Outcome.Verified: return (true, null);
            case Outcome.Declined: return (false, null);
        }
        return Password(hwnd, reason);
    }

    // MARK: - Windows Hello (UserConsentVerifier)

    private static readonly Guid IUserConsentVerifierInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    /// <summary>IAsyncOperation&lt;UserConsentVerificationResult&gt; (a WinRT parameterized IID).</summary>
    private static readonly Guid IAsyncOperationOfResult = new("fd596ffd-2318-558f-9dbe-d21df43764a5");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestVerificationForWindowAsync(IntPtr self, IntPtr hwnd, IntPtr message, ref Guid riid, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStatus(IntPtr self, out int status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResults(IntPtr self, out int result);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static async Task<Outcome> HelloAsync(IntPtr hwnd, string message)
    {
        IntPtr className = IntPtr.Zero, text = IntPtr.Zero, factory = IntPtr.Zero, operation = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            const string name = "Windows.Security.Credentials.UI.UserConsentVerifier";
            if (WindowsCreateString(name, name.Length, out className) < 0) return Outcome.Unavailable;
            var iid = IUserConsentVerifierInterop;
            if (RoGetActivationFactory(className, ref iid, out factory) < 0 || factory == IntPtr.Zero) return Outcome.Unavailable;
            if (WindowsCreateString(message, message.Length, out text) < 0) return Outcome.Unavailable;
            var operationIid = IAsyncOperationOfResult;
            // Slots 0–5 are IUnknown + IInspectable.
            if (Method<RequestVerificationForWindowAsync>(factory, 6)(factory, hwnd, text, ref operationIid, out operation) < 0
                || operation == IntPtr.Zero) return Outcome.Unavailable;
            var infoIid = IAsyncInfo;
            if (Marshal.QueryInterface(operation, in infoIid, out info) < 0) return Outcome.Unavailable;
            var status = 0;
            var getStatus = Method<GetStatus>(info, 7);
            // The prompt is its own process; poll instead of wiring a WinRT completion handler.
            while (getStatus(info, out status) >= 0 && status == 0) await Task.Delay(120);
            if (status != 1) return status == 2 ? Outcome.Declined : Outcome.Unavailable; // 2 = canceled, 3 = error
            if (Method<GetResults>(operation, 8)(operation, out var result) < 0) return Outcome.Unavailable;
            return result switch
            {
                0 => Outcome.Verified,
                // DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy: fall back to the password.
                1 or 2 or 3 => Outcome.Unavailable,
                // DeviceBusy, RetriesExhausted, Canceled.
                _ => Outcome.Declined,
            };
        }
        catch (Exception)
        {
            return Outcome.Unavailable;
        }
        finally
        {
            if (info != IntPtr.Zero) Marshal.Release(info);
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (text != IntPtr.Zero) WindowsDeleteString(text);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    // MARK: - Windows password (CredUI)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public IntPtr Parent;
        public string Message;
        public string Caption;
        public IntPtr Banner;
    }

    private const int CredUiWinEnumerateCurrentUser = 0x200;
    private const int ErrorCancelled = 1223;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CredUiInfo info, int authError, ref uint authPackage,
        IntPtr inBuffer, uint inBufferSize, out IntPtr outBuffer, out uint outBufferSize, ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, StringBuilder user, ref int userLength,
        StringBuilder domain, ref int domainLength, StringBuilder password, ref int passwordLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pointer);

    private static (bool Ok, string? Error) Password(IntPtr hwnd, string reason)
    {
        try
        {
            var info = new CredUiInfo
            {
                Size = Marshal.SizeOf<CredUiInfo>(),
                Parent = hwnd,
                Caption = "DSH Credentials Vault",
                Message = $"Enter your Windows password to {reason}.",
            };
            var error = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                uint package = 0;
                var save = false;
                var result = CredUIPromptForWindowsCredentials(ref info, error, ref package, IntPtr.Zero, 0,
                    out var buffer, out var size, ref save, CredUiWinEnumerateCurrentUser);
                if (result == ErrorCancelled) return (false, null);
                if (result != 0) return (true, null); // no prompt on this PC: nothing to check against
                try
                {
                    int userLength = 256, domainLength = 256, passwordLength = 512;
                    var user = new StringBuilder(userLength);
                    var domain = new StringBuilder(domainLength);
                    var password = new StringBuilder(passwordLength);
                    if (!CredUnPackAuthenticationBuffer(0, buffer, size, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
                        return (false, "Windows couldn't read what was entered.");
                    var (account, accountDomain) = Split(user.ToString(), domain.ToString());
                    if (LogonUser(account, accountDomain, password.ToString(), 2 /* interactive */, 0, out var token))
                    {
                        CloseHandle(token);
                        return (true, null);
                    }
                    var failure = Marshal.GetLastWin32Error();
                    password.Clear();
                    // An account with no password can't be checked.
                    if (failure == ErrorAccountRestriction) return (true, null);
                    if (failure != ErrorLogonFailure) return (false, $"Windows couldn't check the password (error {failure}).");
                    error = ErrorLogonFailure; // shows "incorrect password" on the next prompt
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                    {
                        // Wipe the packed credentials before freeing them.
                        for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                        CoTaskMemFree(buffer);
                    }
                }
            }
            return (false, "The password was not accepted.");
        }
        catch (Exception) when (!OperatingSystem.IsWindowsVersionAtLeast(6, 0))
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Windows couldn't ask for the password: {ex.Message}");
        }
    }

    private static (string User, string? Domain) Split(string user, string domain)
    {
        if (domain.Length > 0) return (user, domain);
        var slash = user.IndexOf('\\');
        if (slash > 0) return (user[(slash + 1)..], user[..slash]);
        // A Microsoft account ("name@outlook.com") signs in by UPN with no domain.
        return (user, user.Contains('@') ? null : ".");
    }
}
