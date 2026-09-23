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


if (args.Length > 0 && args[0] == "--worker")
{
    await RunWorker(args[1], args[2]);
    return;
}

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

        ApiContext api = GetApiContext(
            nativeRequest.Key,
            nativeRequest.Secret
        );

        ApiResult result = await GetDocumentInfo(
            nativeRequest.Token,
            api,
            client
        );

        byte[] fileBytes = await DownloadFile(
            result.DownloadUrl,
            api,
            client
        );


        string filePath = SaveFile(
            result,
            nativeRequest.Token,
            fileBytes
        );

if (result.FileAction == "readwrite")
{
    StartWorker(nativeRequest.Token, filePath);
}

Process.Start(new ProcessStartInfo
{
    FileName = filePath,
    UseShellExecute = true
});

        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });


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

        WriteResponse(output, new
        {
            ok = false,
            error = ex.Message
        });
    }
}
static void StartWorker(string token, string filePath)
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

    Process.Start(startInfo);
}

static async Task RunWorker(
    string token,
    string filePath)
{
    string fileName = Path.GetFileName(filePath);

    object? automation = CreateUiAutomation();

    bool detected = false;

    for (int i = 0; i < 30; i++)
    {
        if (IsDocumentOpen(fileName, automation))
        {
            detected = true;
            break;
        }

        await Task.Delay(500);
    }

    if (!detected)
        return;

    int closedChecks = 0;

    while (closedChecks < 3)
    {
        await Task.Delay(500);

        if (IsDocumentOpen(fileName, automation))
            closedChecks = 0;
        else
            closedChecks++;
    }

    NativeMethods.MessageBoxW(
        IntPtr.Zero,
        $"{fileName} er lukka.",
        "STARLIMS LocalFS",
        0x40
    );
}

static object? CreateUiAutomation()
{
    try
    {
        Type? type = Type.GetTypeFromCLSID(
            new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")
        );

        return type == null
            ? null
            : Activator.CreateInstance(type);
    }
    catch
    {
        return null;
    }
}


static bool IsDocumentOpen(
    string fileName,
    object? automation)
{
    if (HasWindowWithFileName(fileName))
        return true;

    if (automation == null)
        return false;

    try
    {
        dynamic uia = automation;

        const int UIA_ControlTypePropertyId = 30003;
        const int UIA_TabItemControlTypeId = 50019;
        const int TreeScope_Descendants = 4;

        dynamic condition =
            uia.CreatePropertyCondition(
                UIA_ControlTypePropertyId,
                UIA_TabItemControlTypeId
            );

        dynamic root =
            uia.GetRootElement();

        dynamic tabs =
            root.FindAll(
                TreeScope_Descendants,
                condition
            );

        for (int i = 0; i < tabs.Length; i++)
        {
            dynamic tab = tabs.GetElement(i);

            string name =
                tab.CurrentName ?? "";

            if (name.Contains(
                fileName,
                StringComparison.OrdinalIgnoreCase))
            {
                File.AppendAllText(
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.MyDocuments
                        ),
                        "UIA_TABS.txt"
                    ),
                    name + Environment.NewLine
                );
                return true;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}


static bool HasWindowWithFileName(
    string fileName)
{
    bool found = false;

    NativeMethods.EnumWindows(
        (window, parameter) =>
        {
            if (!NativeMethods.IsWindowVisible(window))
                return true;

            StringBuilder title =
                new(1024);

            NativeMethods.GetWindowTextW(
                window,
                title,
                title.Capacity
            );

            if (title.ToString().Contains(
                fileName,
                StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }

            return true;
        },
        IntPtr.Zero
    );

    return found;
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



static async Task<byte[]> DownloadFile(
    string url,
    ApiContext api,
    HttpClient client)
{
    
    string timestamp =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    using HttpRequestMessage req =
        new(HttpMethod.Get, url);

    req.Content =
        new StringContent("");

    req.Content.Headers.ContentType =
        new MediaTypeHeaderValue("text/plain");

    req.Headers.Add(
        "SL-API-Auth",
        api.AccessKey
    );

    req.Headers.Add(
        "SL-API-Timestamp",
        timestamp
    );

    req.Headers.Add(
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
   using HttpResponseMessage resp =
    await client.SendAsync(req);

byte[] bytes = await resp.Content.ReadAsByteArrayAsync();



if (!resp.IsSuccessStatusCode)
    throw new Exception($"Download failed: {(int)resp.StatusCode}");

return bytes;
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


static string SaveFile(
    ApiResult result,
    string token,
    byte[] fileBytes)
{
    string folder =
        result.FileAction == "readwrite"
            ? Path.Combine(
                result.ClientFilePath,
                token
            )
            : result.ClientFilePath;

    Directory.CreateDirectory(folder);

    string filePath =
        Path.Combine(
            folder,
            Path.GetFileName(
                result.FileName
            )
        );

    File.WriteAllBytes(
        filePath,
        fileBytes
    );

    return filePath;
}


static ApiContext GetApiContext(
    string keyCredentialName,
    string secretCredentialName)
{
    Credential key =
        ReadCredential(
            keyCredentialName
        );

    Credential secret =
        ReadCredential(
            secretCredentialName
        );

    if (!string.Equals(
        key.UserName,
        secret.UserName,
        StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception(
            "API key and secret belong to different environments."
        );
    }

    string apiRoot =
        key.UserName.ToUpperInvariant() switch
        {
            "DEV" =>
                "https://rhs-limsapou-83.ad.ous-hf.no/" +
                "STARLIMS.DEV/rest.web.api/",

            _ => throw new Exception(
                $"Unknown environment: {key.UserName}"
            )
        };

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
        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Credential not found: {target}"
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

delegate bool EnumWindowsProc(
    IntPtr hWnd,
    IntPtr lParam
);
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


[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool EnumWindows(
    EnumWindowsProc callback,
    IntPtr parameter
);


[DllImport(
    "user32.dll",
    CharSet = CharSet.Unicode)]
public static extern int GetWindowTextW(
    IntPtr hWnd,
    StringBuilder text,
    int maxCount
);


[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool IsWindowVisible(
    IntPtr hWnd
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

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
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
