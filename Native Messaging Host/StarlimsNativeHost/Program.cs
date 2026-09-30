using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;


if (args.Length > 0 && args[0] == "--worker")
{
    try
    {
        await RunWorker(
            args[1],
            args[2],
            args[3],
            args[4]
        );
    }
    catch (Exception ex)
    {
        WriteError(ex);

        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            ex.ToString(),
            "STARLIMS LocalFS Worker ERROR",
            0x10
        );
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
    try
    {
        string? json = ReadMessage(input);

        if (json == null)
            break;


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

        ApiContext api = GetApiContext(
            nativeRequest.Name
        );
        ApiResult result = await GetDocumentInfo(
            nativeRequest.Token,
            api,
            client
        );

        string filePath = await DownloadFile(
            result.DownloadUrl,
            result,
            nativeRequest.Token,
            api,
            client
        );
Process.Start(new ProcessStartInfo
{
    FileName = filePath,
    UseShellExecute = true
});


    StartWorker(nativeRequest.Token, filePath, nativeRequest.Name, result.FileAction);



        WriteResponse(output, new
        {
            ok = true,
            fileAction = result.FileAction,
            filePath
        });
    }
catch (Exception ex)
{
    WriteError(ex);

    NativeMethods.MessageBoxW(
        IntPtr.Zero,
        ex.ToString(),
        "STARLIMS LocalFS ERROR",
        0x10
    );

    WriteResponse(output, new
    {
        ok = false,
        error = ex.Message
    });
}
}
static void StartWorker(
    string token,
    string filePath,
    string name,
    string actionType)
{
    ProcessStartInfo startInfo = new()
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    startInfo.ArgumentList.Add("--worker");
    startInfo.ArgumentList.Add(token);
    startInfo.ArgumentList.Add(filePath);
    startInfo.ArgumentList.Add(name);
    startInfo.ArgumentList.Add(actionType);

    Process process =
        Process.Start(startInfo)
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
            await RunReadWriteWorker(
                token,
                filePath,
                name
            );
            break;

        case "read":
            await RunReadWorker(
                token,
                filePath,
                name
            );
            break;

        case "saveopen":
            await RunSaveOpenWorker(
                token,
                filePath,
                name
            );
            break;

        default:
            throw new Exception(
                $"Unknown file action: {actionType}"
            );
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

    DateTime lastWriteTime =
        File.GetLastWriteTimeUtc(filePath);

    while (IsFileOpen(filePath))
    {
        DateTime currentWriteTime =
            File.GetLastWriteTimeUtc(filePath);

        if (currentWriteTime != lastWriteTime)
        {
            await UploadFile(
                token,
                filePath,
                name
            );

            lastWriteTime = currentWriteTime;
        }

        await Task.Delay(250);
    }

    DateTime finalWriteTime =
        File.GetLastWriteTimeUtc(filePath);

    if (finalWriteTime != lastWriteTime)
    {
        await UploadFile(
            token,
            filePath,
            name
        );
    }

    await EndClientFileHandling(
        token,
        name
    );

    string folderPath =
        Path.GetDirectoryName(filePath)!;

    File.Delete(filePath);

    Directory.Delete(
        folderPath,
        true
    );
}

static async Task RunReadWorker(
    string token,
    string filePath,
    string name)
{
       while (!IsFileOpen(filePath))
    {
        await Task.Delay(250);
    }

    DateTime lastWriteTime =
        File.GetLastWriteTimeUtc(filePath);

    while (IsFileOpen(filePath))
    {
           await Task.Delay(250);
    }

   

    string folderPath =
        Path.GetDirectoryName(filePath)!;

    File.Delete(filePath);

    Directory.Delete(
        folderPath,
        true
    );
}

static async Task RunSaveOpenWorker(
    string token,
    string filePath,
    string name)
{
       while (!IsFileOpen(filePath))
    {
        await Task.Delay(250);
    }

}


static async Task UploadFile(
    string token,
    string filePath,
    string name)
{
    string? snapshotPath = null;

    try
    {
        snapshotPath =
            await CreateUploadSnapshot(filePath);

        ApiContext api = GetApiContext(name);

        using HttpClientHandler handler = new()
        {
            UseDefaultCredentials = true
        };

        using HttpClient client =
            new(handler);

        string url =
            api.ApiRoot +
            "v1/Folders/getDocumentFromClient";

        string prefix =
            "{\"token\":" +
            JsonSerializer.Serialize(token) +
            ",\"file\":\"";

        string timestamp =
            DateTime.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ"
            );

        string signature =
            await ComputeUploadSignature(
                url,
                api.AccessKey,
                timestamp,
                prefix,
                snapshotPath,
                api.SecretKey
            );

        using HttpRequestMessage request =
            new(HttpMethod.Post, url);

        request.Content =
            new StreamingJsonFileContent(
                prefix,
                snapshotPath
            );

        request.Headers.Add(
            "SL-API-Auth",
            api.AccessKey
        );

        request.Headers.Add(
            "SL-API-Timestamp",
            timestamp
        );

        request.Headers.Add(
            "SL-API-Signature",
            signature
        );

        using HttpResponseMessage response =
            await client.SendAsync(request);

        string responseBody =
            await response.Content.ReadAsStringAsync();


        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(
                $"Upload failed: {(int)response.StatusCode} " +
                $"{response.StatusCode}\n{responseBody}"
            );
        }
    }
    catch (Exception ex)
    {
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            ex.ToString(),
            "STARLIMS Upload ERROR",
            0x10
        );

        WriteError(ex);

        throw;
    }
    finally
    {
        if (snapshotPath != null)
        {
            try
            {
                File.Delete(snapshotPath);
            }
            catch
            {
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

    using HttpClient client =
        new(handler);

    string url =
        api.ApiRoot +
        "v1/Folders/endClientFileHandling";

    string json =
        JsonSerializer.Serialize(new
        {
            token
        });

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ"
        );

    using HttpRequestMessage request =
        new(HttpMethod.Post, url);

    request.Content =
        new StringContent(
            json,
            Encoding.UTF8,
            "application/json"
        );

    request.Headers.Add(
        "SL-API-Auth",
        api.AccessKey
    );

    request.Headers.Add(
        "SL-API-Timestamp",
        timestamp
    );

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

    using HttpResponseMessage response =
        await client.SendAsync(request);

    string body =
        await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"End file handling failed: " +
            $"{(int)response.StatusCode} " +
            $"{response.StatusCode}\n{body}"
        );
    }
}
static async Task<string> CreateUploadSnapshot(
    string filePath)
{
    string snapshotPath =
        filePath + ".uploading";

    while (true)
    {
        try
        {
            DateTime writeTimeBefore =
                File.GetLastWriteTimeUtc(filePath);

            long lengthBefore =
                new FileInfo(filePath).Length;

            await using (
                FileStream input = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete,
                    81920,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan
                )
            )
            await using (
                FileStream output = new(
                    snapshotPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan
                )
            )
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }

            DateTime writeTimeAfter =
                File.GetLastWriteTimeUtc(filePath);

            long lengthAfter =
                new FileInfo(filePath).Length;

            if (
                writeTimeBefore == writeTimeAfter &&
                lengthBefore == lengthAfter
            )
            {
                return snapshotPath;
            }
        }
        catch (IOException)
        {
        }

        try
        {
            File.Delete(snapshotPath);
        }
        catch
        {
        }

        await Task.Delay(100);
    }
}
static class WorkerRegistry
{
    private static readonly ConcurrentDictionary<int, Process> workers = new();

    private static DateTime lastPing =
        DateTime.UtcNow;

    private static Timer? timer;

    public static void RegisterPing()
    {
        lastPing = DateTime.UtcNow;
    }

    public static void Register(Process process)
    {
        process.EnableRaisingEvents = true;

        workers[process.Id] = process;

        process.Exited += (_, _) =>
        {
            workers.TryRemove(
                process.Id,
                out _
            );

            process.Dispose();
        };

        if (process.HasExited)
        {
            workers.TryRemove(
                process.Id,
                out _
            );

            process.Dispose();
        }
    }

    public static void StartIdleShutdown()
    {
        timer = new Timer(
            _ =>
            {
                TimeSpan idleTime =
                    DateTime.UtcNow - lastPing;

                if (
                    idleTime >= TimeSpan.FromMinutes(3) &&
                    workers.IsEmpty
                )
                {
                    Environment.Exit(0);
                }
            },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
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
    byte[] metadata =
        Encoding.UTF8.GetBytes(
            $"{url}\n" +
            $"POST\n" +
            $"{accessKey}\n" +
            $"\n" +
            $"{timestamp}\n"
        );

    byte[] prefixBytes =
        Encoding.UTF8.GetBytes(prefix);

    byte[] suffixBytes =
        Encoding.UTF8.GetBytes("\"}");

    using IncrementalHash hash =
        IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secretKey)
        );

    hash.AppendData(metadata);
    hash.AppendData(prefixBytes);

    await using FileStream input = new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        81920,
        FileOptions.Asynchronous |
        FileOptions.SequentialScan
    );

    await using HashWriteStream hashStream =
        new(hash);

    using ToBase64Transform transform = new();

    await using (
        CryptoStream base64Stream = new(
            hashStream,
            transform,
            CryptoStreamMode.Write,
            true
        )
    )
    {
        await input.CopyToAsync(base64Stream);
    }

    hash.AppendData(suffixBytes);

    byte[] signature =
        hash.GetHashAndReset();

    return WebUtility.UrlEncode(
        Convert.ToBase64String(signature)
    );
}

static bool IsFileOpen(string filePath)
{
    try
    {
        using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );

        return false;
    }
    catch (IOException)
    {
        return true;
    }
}


static async Task<ApiResult> GetDocumentInfo(
    string token,
    ApiContext api,
    HttpClient client)
{
    string url =
    api.ApiRoot +
    "v1/Folders/SendDocumentToClient" +
    $"?token={WebUtility.UrlEncode(token)}" +
    $"&accessKey={WebUtility.UrlEncode(api.AccessKey)}";

    using HttpRequestMessage request =
        CreateSignedGet(
            url,
            "application/json",
            api
        );

    request.Headers.Accept.Add(
        new MediaTypeWithQualityHeaderValue(
            "application/json"
        )
    );

    using HttpResponseMessage response =
        await client.SendAsync(request);

    string body =
        await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"Metadata API returned {(int)response.StatusCode}: {body}"
        );
    }

    ApiResponse apiResponse =
        JsonSerializer.Deserialize<ApiResponse>(body)
        ?? throw new Exception(
            "Invalid API response."
        );

    return apiResponse.Result.FirstOrDefault()
        ?? throw new Exception(
            "API returned no result."
        );
}



static async Task<string> DownloadFile(
    string url,
    ApiResult result,
    string token,
    ApiContext api,
    HttpClient client)
{
    string folder =
        result.FileAction == "readwrite" ||  result.FileAction == "read"
            ? Path.Combine(
                result.ClientFilePath,
                token
            )
            : result.ClientFilePath;

    Directory.CreateDirectory(folder);

    string filePath =
        Path.Combine(
            folder,
            Path.GetFileName(result.FileName)
        );

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ"
        );

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Content =
        new StringContent("");

    request.Content.Headers.ContentType =
        new MediaTypeHeaderValue("text/plain");

    request.Headers.Add(
        "SL-API-Auth",
        api.AccessKey
    );

    request.Headers.Add(
        "SL-API-Timestamp",
        timestamp
    );

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

    using HttpResponseMessage response =
        await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"Download failed: {(int)response.StatusCode}"
        );
    }

    await using Stream input =
        await response.Content.ReadAsStreamAsync();

    await using FileStream output = new(
        filePath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        81920,
        FileOptions.Asynchronous |
        FileOptions.SequentialScan
    );

    await input.CopyToAsync(output);

    return filePath;
}


static HttpRequestMessage CreateSignedGet(
    string url,
    string contentType,
    ApiContext api)
{
    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string signature =
        ComputeSignature(
            url,
            "GET",
            api.AccessKey,
            timestamp,
            "",
            api.SecretKey
        );

    HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Content =
        new ByteArrayContent(
            Array.Empty<byte>()
        );

    request.Content.Headers.ContentType =
        new MediaTypeHeaderValue(
            contentType
        );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Auth",
        api.AccessKey
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Timestamp",
        timestamp
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Signature",
        signature
    );

    return request;
}


static string ComputeSignature(
    string url,
    string method,
    string accessKey,
    string timestamp,
    string payload,
    string secretKey)
{
    string data =
        $"{url}\n" +
        $"{method}\n" +
        $"{accessKey}\n" +
        $"\n" +
        $"{timestamp}\n" +
        $"{payload}";

    using HMACSHA256 hmac =
        new(
            Encoding.UTF8.GetBytes(
                secretKey
            )
        );

    byte[] hash =
        hmac.ComputeHash(
            Encoding.UTF8.GetBytes(
                data
            )
        );

    return WebUtility.UrlEncode(
        Convert.ToBase64String(hash)
    );
}





static ApiContext GetApiContext(
    string name)
{
    string apiRoot =
        name.EndsWith("/")
            ? name
            : name + "/";

    Credential key =
        ReadCredential(
            apiRoot + "_API"
        );

    Credential secret =
        ReadCredential(
            apiRoot + "_SECRET"
        );

    return new ApiContext(
        apiRoot,
        key.Password,
        secret.Password
    );
}


static Credential ReadCredential(
    string target)
{
    if (!NativeMethods.CredReadW(
        target,
        1,
        0,
        out IntPtr pointer))
    {
        int errorCode =
            Marshal.GetLastWin32Error();

        throw new Win32Exception(
            errorCode,
            $"Credential lookup failed.\n" +
            $"Target: {target}\n" +
            $"Windows error: {errorCode}"
        );
    }

    try
    {
        NativeCredential credential =
            Marshal.PtrToStructure<NativeCredential>(
                pointer
            );

        string userName =
            Marshal.PtrToStringUni(
                credential.UserName
            ) ?? "";

        byte[] bytes =
            new byte[
                credential.CredentialBlobSize
            ];

        Marshal.Copy(
            credential.CredentialBlob,
            bytes,
            0,
            bytes.Length
        );

        string password =
            Encoding.Unicode
                .GetString(bytes)
                .TrimEnd('\0');

        return new Credential(
            userName,
            password
        );
    }
    finally
    {
        NativeMethods.CredFree(pointer);
    }
}


static void WriteError(
    Exception ex)
{
    File.WriteAllText(
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments
            ),
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_ERROR.txt"
        ),
        ex.ToString()
    );
}


static string? ReadMessage(
    Stream input)
{
    byte[] lengthBytes =
        new byte[4];

    if (ReadExact(
        input,
        lengthBytes,
        4) == 0)
    {
        return null;
    }

    int length =
        BitConverter.ToInt32(
            lengthBytes,
            0
        );

    byte[] data =
        new byte[length];

    if (ReadExact(
        input,
        data,
        length) != length)
    {
        throw new Exception(
            "Incomplete native message."
        );
    }

    return Encoding.UTF8.GetString(data);
}


static int ReadExact(
    Stream stream,
    byte[] buffer,
    int count)
{
    int total = 0;

    while (total < count)
    {
        int read =
            stream.Read(
                buffer,
                total,
                count - total
            );

        if (read == 0)
            return total;

        total += read;
    }

    return total;
}


static void WriteResponse(
    Stream output,
    object response)
{
    byte[] data =
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                response
            )
        );

    output.Write(
        BitConverter.GetBytes(
            data.Length
        )
    );

    output.Write(data);
    output.Flush();
}

static class NativeMethods
{
    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredReadW(
        string target,
        uint type,
        uint flags,
        out IntPtr credential
    );

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredFree")]
    public static extern void CredFree(
        IntPtr buffer
    );

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        uint type
    );
}

[StructLayout(
    LayoutKind.Sequential,
    CharSet = CharSet.Unicode)]
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


sealed record Credential(
    string UserName,
    string Password
);


sealed record ApiContext(
    string ApiRoot,
    string AccessKey,
    string SecretKey
);


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
sealed class HashWriteStream : Stream
{
    private readonly IncrementalHash hash;

    public HashWriteStream(
        IncrementalHash hash)
    {
        this.hash = hash;
    }

    public override void Write(
        byte[] buffer,
        int offset,
        int count)
    {
        hash.AppendData(
            buffer,
            offset,
            count
        );
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
        hash.AppendData(
            buffer,
            offset,
            count
        );

        return Task.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length =>
        throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(
        long offset,
        SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(
        long value)
    {
        throw new NotSupportedException();
    }
}


sealed class StreamingJsonFileContent : HttpContent
{
    private readonly string prefix;
    private readonly string filePath;

    public StreamingJsonFileContent(
        string prefix,
        string filePath)
    {
        this.prefix = prefix;
        this.filePath = filePath;

        Headers.ContentType =
            new MediaTypeHeaderValue(
                "application/json"
            );
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        byte[] prefixBytes =
            Encoding.UTF8.GetBytes(prefix);

        await stream.WriteAsync(prefixBytes);

        await using FileStream input = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan
        );

        using ToBase64Transform transform = new();

        await using (
            CryptoStream base64Stream = new(
                stream,
                transform,
                CryptoStreamMode.Write,
                true
            )
        )
        {
            await input.CopyToAsync(base64Stream);
        }

        await stream.WriteAsync(
            Encoding.UTF8.GetBytes("\"}")
        );
    }

    protected override bool TryComputeLength(
        out long length)
    {
        long fileLength =
            new FileInfo(filePath).Length;

        long base64Length =
            ((fileLength + 2) / 3) * 4;

        length =
            Encoding.UTF8.GetByteCount(prefix) +
            base64Length +
            2;

        return true;
    }
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
