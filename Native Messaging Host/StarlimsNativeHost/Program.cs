using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

if (args.Length > 0 && args[0] == "--worker")
{
    try
    {
        if (args.Length != 5)
        {
            throw new ArgumentException("Invalid worker arguments.");
        }

        await RunWorker(args[1], args[2], args[3], args[4]);
    }
    catch (Exception ex)
    {
        ShowError(ex, "STARLIMS LocalFS Worker ERROR");
    }

    return;
}

WorkerRegistry.StartIdleShutdown();

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

using HttpClientHandler handler = new()
{
    UseDefaultCredentials = true
};

using HttpClient client = new(handler);

while (true)
{
    string? json;

    try
    {
        json = ReadMessage(input);
    }
    catch (Exception ex)
    {
        ShowError(ex, "STARLIMS LocalFS ERROR");
        break;
    }

    if (json == null)
    {
        break;
    }

    using IDisposable activeRequest = WorkerRegistry.BeginRequest();

    try
    {
        NativeRequest nativeRequest =
            JsonSerializer.Deserialize<NativeRequest>(json)
            ?? throw new Exception("Invalid request.");

        if (nativeRequest.Type == "ping")
        {
            WorkerRegistry.RegisterPing();

            WriteResponse(output, new
            {
                ok = true,
                type = "pong"
            });

            continue;
        }

        ApiContext api = GetApiContext(nativeRequest.Name);

        ApiResult document = await GetDocumentInfo(
            nativeRequest.Token,
            api,
            client
        );

        FileActionResult actionResult = await ExecuteFileAction(
            nativeRequest.Token,
            nativeRequest.Name,
            document,
            api,
            client
        );

        WriteResponse(output, new
        {
            ok = true,
            fileAction = document.FileAction,
            filePath = actionResult.FilePath,
            cancelled = actionResult.Cancelled
        });
    }
    catch (Exception ex)
    {
        ShowError(ex, "STARLIMS LocalFS ERROR");

        WriteResponse(output, new
        {
            ok = false,
            error = ex.Message
        });
    }
}

static async Task<FileActionResult> ExecuteFileAction(
    string token,
    string name,
    ApiResult document,
    ApiContext api,
    HttpClient client)
{
    string actionType = document.FileAction.Trim().ToLowerInvariant();

    switch (actionType)
    {
        case "readwrite":
        case "read":
        {
            string filePath = await DownloadFile(
                document.DownloadUrl,
                document,
                token,
                api,
                client,
                useTokenFolder: true
            );

            if (actionType == "read")
            {
                await EndClientFileHandling(token, name);
            }

            OpenDocument(filePath);

            StartWorker(
                token,
                filePath,
                name,
                actionType
            );

            return new FileActionResult(filePath);
        }

        case "save":
        {
            string filePath = await DownloadFile(
                document.DownloadUrl,
                document,
                token,
                api,
                client,
                useTokenFolder: false
            );

            await EndClientFileHandling(token, name);

            return new FileActionResult(filePath);
        }

        case "saveopen":
        {
            string filePath = await DownloadFile(
                document.DownloadUrl,
                document,
                token,
                api,
                client,
                useTokenFolder: false
            );

            await EndClientFileHandling(token, name);

            OpenDocument(filePath);

            return new FileActionResult(filePath);
        }

        case "upload":
        {
            string path = Environment.ExpandEnvironmentVariables(document.ClientFilePath);

            bool isFile = File.Exists(path);

            string? filePath;

            if (isFile){
                filePath = path;
            }
            else
            {
                filePath = await SelectUploadFile(
                    path
                );
            }

            if (filePath == null)
            {
                return new FileActionResult(null, Cancelled: true);
            }

            await UploadFile(token, filePath, name);
            await EndClientFileHandling(token, name);

            return new FileActionResult(filePath);
        }

        default:
            throw new Exception(
                $"Unknown file action: {document.FileAction}"
            );
    }
}

static void OpenDocument(string filePath)
{
    Process.Start(new ProcessStartInfo
    {
        FileName = filePath,
        UseShellExecute = true
    });
}

static void StartWorker(
    string token,
    string filePath,
    string name,
    string actionType)
{
    ProcessStartInfo startInfo = new()
    {
        FileName = Environment.ProcessPath
            ?? throw new Exception("Cannot locate the native host executable."),
        UseShellExecute = false,
        CreateNoWindow = true
    };

    startInfo.ArgumentList.Add("--worker");
    startInfo.ArgumentList.Add(token);
    startInfo.ArgumentList.Add(filePath);
    startInfo.ArgumentList.Add(name);
    startInfo.ArgumentList.Add(actionType);

    Process process = Process.Start(startInfo)
        ?? throw new Exception("Failed to start worker.");

    WorkerRegistry.Register(process);
}

static async Task RunWorker(
    string token,
    string filePath,
    string name,
    string actionType)
{
    switch (actionType.ToLowerInvariant())
    {
        case "readwrite":
            await RunReadWriteWorker(token, filePath, name);
            break;

        case "read":
            await RunReadWorker(filePath);
            break;

        default:
            throw new Exception($"Unknown worker action: {actionType}");
    }
}

static async Task RunReadWriteWorker(
    string token,
    string filePath,
    string name)
{
    while (!IsFileOpen(filePath))
    {
        await Task.Delay(250);
    }

    DateTime lastWriteTime = File.GetLastWriteTimeUtc(filePath);

    while (IsFileOpen(filePath))
    {
        DateTime currentWriteTime = File.GetLastWriteTimeUtc(filePath);

        if (currentWriteTime != lastWriteTime)
        {
            await UploadFile(token, filePath, name);
            lastWriteTime = currentWriteTime;
        }

        await Task.Delay(250);
    }

    DateTime finalWriteTime = File.GetLastWriteTimeUtc(filePath);

    if (finalWriteTime != lastWriteTime)
    {
        await UploadFile(token, filePath, name);
    }

    await EndClientFileHandling(token, name);

    string folderPath = Path.GetDirectoryName(filePath)!;

    File.Delete(filePath);
    Directory.Delete(folderPath, true);
}

static async Task RunReadWorker(string filePath)
{
    while (!IsFileOpen(filePath))
    {
        await Task.Delay(250);
    }

    while (IsFileOpen(filePath))
    {
        await Task.Delay(250);
    }

    string folderPath = Path.GetDirectoryName(filePath)!;

    File.Delete(filePath);
    Directory.Delete(folderPath, true);
}

static Task<string?> SelectUploadFile(string initialDirectory)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "File selection requires Windows."
        );
    }

    TaskCompletionSource<string?> completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    IntPtr ownerWindow = NativeMethods.GetForegroundWindow();

    Thread dialogThread = new(() =>
    {
        IntPtr fileBuffer = IntPtr.Zero;

        try
        {
            const int bufferCapacity = 32768;

            fileBuffer = Marshal.AllocHGlobal(
                bufferCapacity * sizeof(char)
            );

            Marshal.WriteInt16(fileBuffer, 0);

            NativeOpenFileName dialog = new()
            {
                StructSize = (uint)Marshal.SizeOf<NativeOpenFileName>(),
                OwnerWindow = ownerWindow,
                Filter = "All files (*.*)\0*.*\0\0",
                FilterIndex = 1,
                File = fileBuffer,
                MaxFile = bufferCapacity,
                InitialDirectory = Directory.Exists(initialDirectory)
                    ? initialDirectory
                    : null,
                Title = "STARLIMS - Select a file to upload",
                Flags = NativeMethods.OFN_EXPLORER
                    | NativeMethods.OFN_FILEMUSTEXIST
                    | NativeMethods.OFN_PATHMUSTEXIST
                    | NativeMethods.OFN_HIDEREADONLY
                    | NativeMethods.OFN_NOCHANGEDIR
            };

            if (NativeMethods.GetOpenFileNameW(ref dialog))
            {
                string filePath = Marshal.PtrToStringUni(fileBuffer)
                    ?? throw new Exception("The file dialog returned no path.");

                completion.SetResult(filePath);
                return;
            }

            uint errorCode = NativeMethods.CommDlgExtendedError();

            if (errorCode != 0)
            {
                throw new Exception(
                    $"File selection failed. Dialog error: 0x{errorCode:X}."
                );
            }

            completion.SetResult(null);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            if (fileBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileBuffer);
            }
        }
    })
    {
        IsBackground = true
    };

    dialogThread.SetApartmentState(ApartmentState.STA);
    dialogThread.Start();

    return completion.Task;
}

static async Task UploadFile(
    string token,
    string filePath,
    string name)
{
    string? snapshotPath = null;

    try
    {
        snapshotPath = await CreateUploadSnapshot(filePath);

        ApiContext api = GetApiContext(name);

        using HttpClientHandler handler = new()
        {
            UseDefaultCredentials = true
        };

        using HttpClient client = new(handler);

        string url = api.ApiRoot + "v1/Folders/getDocumentFromClient";

        string prefix = "{\"token\":"
            + JsonSerializer.Serialize(token)
            + ",\"file\":\"";

        string timestamp = CreateTimestamp();

        string signature = await ComputeUploadSignature(
            url,
            api.AccessKey,
            timestamp,
            prefix,
            snapshotPath,
            api.SecretKey
        );

        using HttpRequestMessage request = new(HttpMethod.Post, url);

        request.Content = new StreamingJsonFileContent(
            prefix,
            snapshotPath
        );

        request.Headers.Add("SL-API-Auth", api.AccessKey);
        request.Headers.Add("SL-API-Timestamp", timestamp);
        request.Headers.Add("SL-API-Signature", signature);

        using HttpResponseMessage response = await client.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(
                $"Upload failed: {(int)response.StatusCode} "
                + $"{response.StatusCode}\n{body}"
            );
        }
    }
    finally
    {
        if (snapshotPath != null)
        {
            try
            {
                File.Delete(snapshotPath);
            }
            catch (Exception ex)
            {
                WriteError(ex);
            }
        }
    }
}

static async Task EndClientFileHandling(string token, string name)
{
    ApiContext api = GetApiContext(name);

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client = new(handler);

    string url = api.ApiRoot + "v1/Folders/endClientFileHandling";
    string json = JsonSerializer.Serialize(new { token });
    string timestamp = CreateTimestamp();

    using HttpRequestMessage request = new(HttpMethod.Post, url);

    request.Content = new StringContent(
        json,
        Encoding.UTF8,
        "application/json"
    );

    request.Headers.Add("SL-API-Auth", api.AccessKey);
    request.Headers.Add("SL-API-Timestamp", timestamp);
    request.Headers.Add(
        "SL-API-Signature",
        ComputeSignature(
            url,
            "POST",
            api.AccessKey,
            timestamp,
            json,
            api.SecretKey
        )
    );

    using HttpResponseMessage response = await client.SendAsync(request);

    string body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"End file handling failed: {(int)response.StatusCode} "
            + $"{response.StatusCode}\n{body}"
        );
    }
}

static async Task<string> CreateUploadSnapshot(string filePath)
{
    string snapshotDirectory = Path.Combine(
        Path.GetTempPath(),
        "STARLIMS LocalFS",
        "Uploads"
    );

    Directory.CreateDirectory(snapshotDirectory);

    string snapshotPath = Path.Combine(
        snapshotDirectory,
        Guid.NewGuid().ToString("N") + ".uploading"
    );

    Stopwatch retryTimer = Stopwatch.StartNew();
    IOException? lastError = null;

    try
    {
        while (retryTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                DateTime writeTimeBefore = File.GetLastWriteTimeUtc(filePath);
                long lengthBefore = new FileInfo(filePath).Length;

                await using (FileStream input = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                ))
                await using (FileStream output = new(
                    snapshotPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                ))
                {
                    await input.CopyToAsync(output);
                    await output.FlushAsync();
                }

                DateTime writeTimeAfter = File.GetLastWriteTimeUtc(filePath);
                long lengthAfter = new FileInfo(filePath).Length;

                if (writeTimeBefore == writeTimeAfter && lengthBefore == lengthAfter)
                {
                    return snapshotPath;
                }
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                lastError = ex;
            }

            await Task.Delay(100);
        }

        throw new IOException(
            "Cannot create a stable upload copy within 30 seconds. "
            + "Close the file or wait for it to finish saving, then try again.",
            lastError
        );
    }
    catch
    {
        try
        {
            File.Delete(snapshotPath);
        }
        catch (Exception ex)
        {
            WriteError(ex);
        }

        throw;
    }
}

static async Task<string> ComputeUploadSignature(
    string url,
    string accessKey,
    string timestamp,
    string prefix,
    string filePath,
    string secretKey)
{
    byte[] metadata = Encoding.UTF8.GetBytes(
        $"{url}\nPOST\n{accessKey}\n\n{timestamp}\n"
    );

    using IncrementalHash hash = IncrementalHash.CreateHMAC(
        HashAlgorithmName.SHA256,
        Encoding.UTF8.GetBytes(secretKey)
    );

    hash.AppendData(metadata);
    hash.AppendData(Encoding.UTF8.GetBytes(prefix));

    await using FileStream input = new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );

    await using HashWriteStream hashStream = new(hash);
    using ToBase64Transform transform = new();

    await using (CryptoStream base64Stream = new(
        hashStream,
        transform,
        CryptoStreamMode.Write,
        true
    ))
    {
        await input.CopyToAsync(base64Stream);
    }

    hash.AppendData(Encoding.UTF8.GetBytes("\"}"));

    return WebUtility.UrlEncode(
        Convert.ToBase64String(hash.GetHashAndReset())
    );
}

static bool IsSharingViolation(IOException exception)
{
    int errorCode = exception.HResult & 0xFFFF;
    return errorCode is 32 or 33;
}

static bool IsFileOpen(string filePath)
{
    try
    {
        using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        return false;
    }
    catch (IOException ex) when (IsSharingViolation(ex))
    {
        return true;
    }
}

static async Task<ApiResult> GetDocumentInfo(
    string token,
    ApiContext api,
    HttpClient client)
{
    string url = api.ApiRoot
        + "v1/Folders/SendDocumentToClient"
        + $"?token={WebUtility.UrlEncode(token)}"
        + $"&accessKey={WebUtility.UrlEncode(api.AccessKey)}";

    using HttpRequestMessage request = CreateSignedGet(
        url,
        "application/json",
        api
    );

    request.Headers.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json")
    );

    using HttpResponseMessage response = await client.SendAsync(request);

    string body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"Metadata API returned {(int)response.StatusCode}: {body}"
        );
    }

    ApiResponse apiResponse = JsonSerializer.Deserialize<ApiResponse>(body)
    ?? throw new Exception("Invalid API response.");

ApiResult result = apiResponse.Result.FirstOrDefault()
    ?? throw new Exception("API returned no result.");

if (string.IsNullOrWhiteSpace(result.FileAction))
{
    throw new Exception(
        $"FILE_ACTION is empty or missing. API response:\n{body}"
    );
}

result.ClientFilePath = Environment.ExpandEnvironmentVariables(
    result.ClientFilePath
);

return result;
}

static async Task<string> DownloadFile(
    string url,
    ApiResult result,
    string token,
    ApiContext api,
    HttpClient client,
    bool useTokenFolder)
{
    string folder = useTokenFolder
        ? Path.Combine(result.ClientFilePath, token)
        : result.ClientFilePath;

    Directory.CreateDirectory(folder);

    string filePath = Path.Combine(
        folder,
        Path.GetFileName(result.FileName)
    );

    string timestamp = CreateTimestamp();

    using HttpRequestMessage request = new(HttpMethod.Get, url);

    request.Content = new StringContent("");
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

    request.Headers.Add("SL-API-Auth", api.AccessKey);
    request.Headers.Add("SL-API-Timestamp", timestamp);
    request.Headers.Add(
        "SL-API-Signature",
        ComputeSignature(
            url,
            "GET",
            api.AccessKey,
            timestamp,
            "",
            api.SecretKey
        )
    );

    using HttpResponseMessage response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead
    );

    if (!response.IsSuccessStatusCode)
    {
        string body = await response.Content.ReadAsStringAsync();

        throw new Exception(
            $"Download failed: {(int)response.StatusCode} "
            + $"{response.StatusCode}\n{body}"
        );
    }

    await using Stream input = await response.Content.ReadAsStreamAsync();

    await using FileStream output = new(
        filePath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan
    );

    await input.CopyToAsync(output);

    return filePath;
}

static HttpRequestMessage CreateSignedGet(
    string url,
    string contentType,
    ApiContext api)
{
    string timestamp = CreateTimestamp();

    HttpRequestMessage request = new(HttpMethod.Get, url);

    request.Content = new ByteArrayContent(Array.Empty<byte>());
    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

    request.Headers.TryAddWithoutValidation("SL-API-Auth", api.AccessKey);
    request.Headers.TryAddWithoutValidation("SL-API-Timestamp", timestamp);
    request.Headers.TryAddWithoutValidation(
        "SL-API-Signature",
        ComputeSignature(
            url,
            "GET",
            api.AccessKey,
            timestamp,
            "",
            api.SecretKey
        )
    );

    return request;
}

static string CreateTimestamp()
{
    return DateTime.UtcNow.ToString(
        "yyyy-MM-ddTHH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture
    );
}

static string ComputeSignature(
    string url,
    string method,
    string accessKey,
    string timestamp,
    string payload,
    string secretKey)
{
    string data = $"{url}\n{method}\n{accessKey}\n\n{timestamp}\n{payload}";

    using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secretKey));

    byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));

    return WebUtility.UrlEncode(Convert.ToBase64String(hash));
}

static ApiContext GetApiContext(string name)
{
    string apiRoot = name.EndsWith("/") ? name : name + "/";

    Credential key = ReadCredential(apiRoot + "_API");
    Credential secret = ReadCredential(apiRoot + "_SECRET");

    return new ApiContext(apiRoot, key.Password, secret.Password);
}

static Credential ReadCredential(string target)
{
    if (!NativeMethods.CredReadW(target, 1, 0, out IntPtr pointer))
    {
        int errorCode = Marshal.GetLastWin32Error();

        throw new Win32Exception(
            errorCode,
            $"Credential lookup failed.\nTarget: {target}\n"
            + $"Windows error: {errorCode}"
        );
    }

    try
    {
        NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(
            pointer
        );

        string userName = Marshal.PtrToStringUni(credential.UserName) ?? "";
        byte[] bytes = new byte[credential.CredentialBlobSize];

        Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

        string password = Encoding.Unicode.GetString(bytes).TrimEnd('\0');

        return new Credential(userName, password);
    }
    finally
    {
        NativeMethods.CredFree(pointer);
    }
}

static void ShowError(Exception exception, string caption)
{
    WriteError(exception);

    NativeMethods.MessageBoxW(
        IntPtr.Zero,
        exception.ToString(),
        caption,
        NativeMethods.MB_ICONERROR
            | NativeMethods.MB_SETFOREGROUND
            | NativeMethods.MB_TOPMOST
    );
}

static void WriteError(Exception exception)
{
    string logName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}"
        + $"_{Environment.ProcessId}_ERROR.txt";

    try
    {
        File.WriteAllText(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                logName
            ),
            exception.ToString()
        );
    }
    catch
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), logName),
                exception.ToString()
            );
        }
        catch
        {
            // Logging must not prevent the error dialog from being shown.
        }
    }
}

static string? ReadMessage(Stream input)
{
    byte[] lengthBytes = new byte[4];
    int prefixLength = ReadExact(input, lengthBytes, lengthBytes.Length);

    if (prefixLength == 0)
    {
        return null;
    }

    if (prefixLength != lengthBytes.Length)
    {
        throw new Exception("Incomplete native message length.");
    }

    int length = BitConverter.ToInt32(lengthBytes, 0);

    if (length <= 0)
    {
        throw new Exception("Invalid native message length.");
    }

    byte[] data = new byte[length];

    if (ReadExact(input, data, length) != length)
    {
        throw new Exception("Incomplete native message.");
    }

    return Encoding.UTF8.GetString(data);
}

static int ReadExact(Stream stream, byte[] buffer, int count)
{
    int total = 0;

    while (total < count)
    {
        int read = stream.Read(buffer, total, count - total);

        if (read == 0)
        {
            return total;
        }

        total += read;
    }

    return total;
}

static void WriteResponse(Stream output, object response)
{
    byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));

    output.Write(BitConverter.GetBytes(data.Length));
    output.Write(data);
    output.Flush();
}

static class WorkerRegistry
{
    private static readonly object syncRoot = new();
    private static readonly Dictionary<int, Process> workers = new();

    private static long lastActivity = Environment.TickCount64;
    private static int activeRequests;
    private static Timer? timer;

    public static IDisposable BeginRequest()
    {
        lock (syncRoot)
        {
            activeRequests++;
            lastActivity = Environment.TickCount64;
        }

        return new RequestScope();
    }

    public static void RegisterPing()
    {
        lock (syncRoot)
        {
            lastActivity = Environment.TickCount64;
        }
    }

    public static void Register(Process process)
    {
        lock (syncRoot)
        {
            workers.Add(process.Id, process);
        }
    }

    public static void StartIdleShutdown()
    {
        timer = new Timer(
            _ =>
            {
                lock (syncRoot)
                {
                    foreach ((int processId, Process process) in workers.ToArray())
                    {
                        if (process.HasExited)
                        {
                            workers.Remove(processId);
                            process.Dispose();
                        }
                    }

                    long idleMilliseconds = Environment.TickCount64 - lastActivity;

                    if (idleMilliseconds >= (long)TimeSpan.FromMinutes(3).TotalMilliseconds
                        && activeRequests == 0
                        && workers.Count == 0)
                    {
                        Environment.Exit(0);
                    }
                }
            },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
    }

    private sealed class RequestScope : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (syncRoot)
            {
                activeRequests--;
                lastActivity = Environment.TickCount64;
            }
        }
    }
}

static class NativeMethods
{
    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_SETFOREGROUND = 0x00010000;
    public const uint MB_TOPMOST = 0x00040000;

    public const uint OFN_HIDEREADONLY = 0x00000004;
    public const uint OFN_NOCHANGEDIR = 0x00000008;
    public const uint OFN_PATHMUSTEXIST = 0x00000800;
    public const uint OFN_FILEMUSTEXIST = 0x00001000;
    public const uint OFN_EXPLORER = 0x00080000;

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredReadW(
        string target,
        uint type,
        uint flags,
        out IntPtr credential
    );

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    public static extern void CredFree(IntPtr buffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        uint type
    );

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("Comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetOpenFileNameW(
        [In, Out] ref NativeOpenFileName openFileName
    );

    [DllImport("Comdlg32.dll", ExactSpelling = true)]
    public static extern uint CommDlgExtendedError();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct NativeOpenFileName
{
    public uint StructSize;
    public IntPtr OwnerWindow;
    public IntPtr Instance;
    public string? Filter;
    public IntPtr CustomFilter;
    public uint MaxCustomFilter;
    public uint FilterIndex;
    public IntPtr File;
    public uint MaxFile;
    public IntPtr FileTitle;
    public uint MaxFileTitle;
    public string? InitialDirectory;
    public string? Title;
    public uint Flags;
    public ushort FileOffset;
    public ushort FileExtension;
    public string? DefaultExtension;
    public IntPtr CustomData;
    public IntPtr Hook;
    public string? TemplateName;
    public IntPtr Reserved;
    public uint ReservedValue;
    public uint FlagsEx;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct NativeCredential
{
    public uint Flags;
    public uint Type;
    public IntPtr TargetName;
    public IntPtr Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    public IntPtr TargetAlias;
    public IntPtr UserName;
}

sealed record Credential(string UserName, string Password);

sealed record ApiContext(
    string ApiRoot,
    string AccessKey,
    string SecretKey
);

sealed record FileActionResult(string? FilePath, bool Cancelled = false);

sealed class NativeRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

sealed class ApiResponse
{
    [JsonPropertyName("Result")]
    public List<ApiResult> Result { get; set; } = [];
}

sealed class ApiResult
{
    [JsonPropertyName("CLIENT_FILE_PATH")]
    public string ClientFilePath { get; set; } = "";

    [JsonPropertyName("FILE_ACTION")]
    public string FileAction { get; set; } = "";

    [JsonPropertyName("FILE_NAME")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("URL")]
    public string DownloadUrl { get; set; } = "";
}

sealed class HashWriteStream : Stream
{
    private readonly IncrementalHash hash;

    public HashWriteStream(IncrementalHash hash)
    {
        this.hash = hash;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        hash.AppendData(buffer, offset, count);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        hash.AppendData(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        hash.AppendData(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}

sealed class StreamingJsonFileContent : HttpContent
{
    private readonly string prefix;
    private readonly string filePath;

    public StreamingJsonFileContent(string prefix, string filePath)
    {
        this.prefix = prefix;
        this.filePath = filePath;

        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix));

        await using FileStream input = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        using ToBase64Transform transform = new();

        await using (CryptoStream base64Stream = new(
            stream,
            transform,
            CryptoStreamMode.Write,
            true
        ))
        {
            await input.CopyToAsync(base64Stream);
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes("\"}"));
    }

    protected override bool TryComputeLength(out long length)
    {
        long fileLength = new FileInfo(filePath).Length;
        long base64Length = ((fileLength + 2) / 3) * 4;

        length = Encoding.UTF8.GetByteCount(prefix) + base64Length + 2;
        return true;
    }
}
