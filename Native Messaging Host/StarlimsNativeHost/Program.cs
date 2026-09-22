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

        Stamp("01_message_received");

        NativeRequest request =
            JsonSerializer.Deserialize<NativeRequest>(json)
            ?? throw new Exception("Invalid request.");

        ApiCredentials credentials =
            GetCredentials(
                request.Key,
                request.Secret
            );

        ApiResult result =
            await GetDocumentInfo(
                request.Token,
                credentials
            );

        Stamp("02_metadata_received");

        byte[] fileBytes =
            await DownloadFile(
                result.DownloadUrl,
                credentials
            );

        Stamp("03_binary_downloaded");

        string filePath =
            SaveFile(
                result,
                request.Token,
                fileBytes
            );

        Stamp("04_file_saved");

        Process.Start(
            new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            }
        );

        Stamp("05_process_started");

        WriteResponse(output, new
        {
            ok = true,
            fileAction = result.FileAction,
            filePath
        });
    }
    catch (Exception ex)
    {
        Stamp("ERROR");

        WriteResponse(output, new
        {
            ok = false,
            error = ex.Message
        });
    }
}


static async Task<ApiResult> GetDocumentInfo(
    string token,
    ApiCredentials credentials)
{
    string url =
        "https://rhs-limsapou-83.ad.ous-hf.no/" +
        "STARLIMS.DEV/rest.web.api/v1/Folders/SendDocumentToClient" +
        $"?token={WebUtility.UrlEncode(token)}";

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string signature =
        ComputeSignature(
            url,
            "GET",
            credentials.AccessKey,
            timestamp,
            "",
            credentials.SecretKey
        );

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client = new(handler);

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Content =
        new ByteArrayContent(
            Array.Empty<byte>()
        );

    request.Content.Headers.ContentType =
        new MediaTypeHeaderValue(
            "application/json"
        );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Auth",
        credentials.AccessKey
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Timestamp",
        timestamp
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Signature",
        signature
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
    ApiCredentials credentials)
{
    if (string.IsNullOrWhiteSpace(url))
    {
        throw new Exception(
            "Download URL is empty."
        );
    }

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string signature =
        ComputeSignature(
            url,
            "GET",
            credentials.AccessKey,
            timestamp,
            "",
            credentials.SecretKey
        );

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client = new(handler);

    using HttpRequestMessage request =
        new(HttpMethod.Get, url);

    request.Content =
        new ByteArrayContent(
            Array.Empty<byte>()
        );

    request.Content.Headers.ContentType =
        new MediaTypeHeaderValue(
            "text/plain"
        );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Auth",
        credentials.AccessKey
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Timestamp",
        timestamp
    );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Signature",
        signature
    );

    using HttpResponseMessage response =
        await client.SendAsync(request);

    if (!response.IsSuccessStatusCode)
    {
        string body =
            await response.Content.ReadAsStringAsync();

        throw new Exception(
            $"Download API returned {(int)response.StatusCode}: {body}"
        );
    }

    return await response.Content
        .ReadAsByteArrayAsync();
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


static string ComputeSignature(
    string url,
    string method,
    string accessKey,
    string timestamp,
    string payload,
    string secretKey)
{
    string stringToSign =
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
                stringToSign
            )
        );

    return WebUtility.UrlEncode(
        Convert.ToBase64String(hash)
    );
}


static ApiCredentials GetCredentials(
    string keyCredentialName,
    string secretCredentialName)
{
    Credential keyCredential =
        ReadCredential(
            keyCredentialName
        );

    Credential secretCredential =
        ReadCredential(
            secretCredentialName
        );

    if (!string.Equals(
        keyCredential.UserName,
        secretCredential.UserName,
        StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception(
            "API key and secret belong to different environments."
        );
    }

    if (!string.Equals(
        keyCredential.UserName,
        "DEV",
        StringComparison.OrdinalIgnoreCase))
    {
        throw new Exception(
            $"Unknown environment: {keyCredential.UserName}"
        );
    }

    return new ApiCredentials(
        keyCredential.Password,
        secretCredential.Password
    );
}


static void Stamp(
    string name)
{
    string fileName =
        $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{name}.txt";

    File.WriteAllText(
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments
            ),
            fileName
        ),
        ""
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


sealed record ApiCredentials(
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
