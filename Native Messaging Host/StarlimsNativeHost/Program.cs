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

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();

while (true)
{
    try
    {
        string? json = ReadMessage(input);

        if (json == null)
            break;

        NativeRequest request =
            JsonSerializer.Deserialize<NativeRequest>(json)
            ?? throw new Exception("Invalid request.");

        ApiConfig config = GetApiConfig(
            request.Key,
            request.Secret
        );

        ApiResult result = await CallApi(
            request.Token,
            config
        );

        byte[] fileBytes = await DownloadFile(
            result.DownloadUrl,
            config
        );

        string filePath = SaveAndOpen(
            result,
            request.Token,
            fileBytes
        );

        if (result.FileAction == "readwrite")
        {
            await WaitUntilOpened(filePath);
            await WaitUntilClosed(filePath);

            // Upload modified file to STARLIMS here later.
        }

        WriteResponse(output, new
        {
            ok = true,
            fileAction = result.FileAction,
            filePath
        });
    }
    catch (Exception ex)
    {
        WriteResponse(output, new
        {
            ok = false,
            error = ex.Message
        });
    }
}

static ApiConfig GetApiConfig(
    string keyCredentialName,
    string secretCredentialName)
{
    Credential keyCredential =
        ReadCredential(keyCredentialName);

    Credential secretCredential =
        ReadCredential(secretCredentialName);

    if (!string.Equals(
        keyCredential.UserName,
        secretCredential.UserName,
        StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception(
            "API key and secret belong to different environments."
        );
    }

    string environment =
        keyCredential.UserName.ToUpperInvariant();

    string apiRoot = environment switch
    {
        "DEV" =>
            "https://rhs-limsapou-83.ad.ous-hf.no/" +
            "STARLIMS.DEV/rest.web.api/",

        _ => throw new Exception(
            $"Unknown environment: {environment}"
        )
    };

    return new ApiConfig(
        apiRoot,
        keyCredential.Password,
        secretCredential.Password
    );
}

static async Task<ApiResult> CallApi(
    string token,
    ApiConfig config)
{
    string url =
        config.ApiRoot +
        "v1/Folders/SendDocumentToClient" +
        $"?token={WebUtility.UrlEncode(token)}";

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client =
        new(handler);

    using HttpRequestMessage request =
        CreateRequest(
            HttpMethod.Get,
            url,
            "application/json",
            config
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
            $"API returned {(int)response.StatusCode}: {body}"
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
    string downloadUrl,
    ApiConfig config)
{
    if (string.IsNullOrWhiteSpace(downloadUrl))
        throw new Exception("DOWNLOAD_URL is missing.");

    Uri url;

    if (Uri.TryCreate(
        downloadUrl,
        UriKind.Absolute,
        out Uri? absoluteUrl))
    {
        url = absoluteUrl;
    }
    else
    {
        url = new Uri(
            new Uri(config.ApiRoot),
            downloadUrl
        );
    }

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client =
        new(handler);

    using HttpRequestMessage request =
        CreateRequest(
            HttpMethod.Get,
            url.AbsoluteUri,
            "text/plain",
            config
        );

    using HttpResponseMessage response =
        await client.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
        string error =
            await response.Content.ReadAsStringAsync();

        throw new Exception(
            $"Download returned {(int)response.StatusCode}: {error}"
        );
    }

    return await response.Content.ReadAsByteArrayAsync();
}

static HttpRequestMessage CreateRequest(
    HttpMethod method,
    string url,
    string contentType,
    ApiConfig config)
{
    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string stringToSign =
        $"{url}\n" +
        $"{method.Method}\n" +
        $"{config.AccessKey}\n" +
        $"\n" +
        $"{timestamp}\n" +
        $"\n";

    string signature;

    using (HMACSHA256 hmac =
        new(
            Encoding.UTF8.GetBytes(
                config.SecretKey
            )
        ))
    {
        byte[] hash =
            hmac.ComputeHash(
                Encoding.UTF8.GetBytes(
                    stringToSign
                )
            );

        signature =
            WebUtility.UrlEncode(
                Convert.ToBase64String(hash)
            );
    }

    HttpRequestMessage request =
        new(method, url);

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
        config.AccessKey
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

static string SaveAndOpen(
    ApiResult result,
    string token,
    byte[] fileBytes)
{
    if (string.IsNullOrWhiteSpace(
        result.ClientFilePath))
    {
        throw new Exception(
            "CLIENT_FILE_PATH is missing."
        );
    }

    if (string.IsNullOrWhiteSpace(
        result.FileName))
    {
        throw new Exception(
            "FILE_NAME is missing."
        );
    }

    string folder;

    if (result.FileAction == "readwrite")
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Token is missing.");

        if (token.Any(
            c => !char.IsLetterOrDigit(c)))
        {
            throw new Exception(
                "Token contains invalid path characters."
            );
        }

        folder = Path.Combine(
            result.ClientFilePath,
            token
        );
    }
    else if (result.FileAction == "read")
    {
        folder =
            result.ClientFilePath;
    }
    else
    {
        throw new Exception(
            $"Unsupported FILE_ACTION: {result.FileAction}"
        );
    }

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

    Process.Start(
        new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        }
    );

    return filePath;
}

static async Task WaitUntilOpened(
    string filePath)
{
    DateTime timeout =
        DateTime.UtcNow.AddSeconds(30);

    while (DateTime.UtcNow < timeout)
    {
        if (!CanOpenExclusive(filePath))
            return;

        await Task.Delay(500);
    }

    throw new Exception(
        "Could not detect that the file was opened."
    );
}

static async Task WaitUntilClosed(
    string filePath)
{
    int closedChecks = 0;

    while (closedChecks < 4)
    {
        await Task.Delay(500);

        if (CanOpenExclusive(filePath))
        {
            closedChecks++;
        }
        else
        {
            closedChecks = 0;
        }
    }
}

static bool CanOpenExclusive(
    string filePath)
{
    try
    {
        using FileStream stream =
            new(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

        return true;
    }
    catch
    {
        return false;
    }
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

    ReadExact(
        input,
        data,
        length
    );

    return Encoding.UTF8.GetString(
        data
    );
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

    byte[] length =
        BitConverter.GetBytes(
            data.Length
        );

    output.Write(length);
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

    public System.Runtime.InteropServices.ComTypes.FILETIME
        LastWritten;

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

sealed record ApiConfig(
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

    [JsonPropertyName("DOWNLOAD_URL")]
    public string DownloadUrl { get; set; } = "";
}
