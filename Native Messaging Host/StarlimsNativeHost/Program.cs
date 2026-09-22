using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();
File.WriteAllText(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt"
    ),
    ""
);

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

        ApiResult result = await CallApi(
            request.Token,
            request.Key,
            request.Secret
        );
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

MessageBox(IntPtr.Zero, "result.downloadURL", "Debug", 0);
        if (result.FileAction == "read")
        {
            string filePath = SaveAndOpen(result);

            WriteResponse(output, new
            {
                ok = true,
                filePath
            });
        }
        else if (result.FileAction == "readwrite")
        {
            string filePath = await SaveOpenAndWatchAsync(
                result,
                request.Token,
                request.Key,
                request.Secret
            );

            WriteResponse(output, new
            {
                ok = true,
                fileAction = result.FileAction,
                filePath
            });
        }
        else
        {
            throw new Exception(
                $"Unsupported FILE_ACTION: {result.FileAction}"
            );
        }
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


static async Task<string> SaveOpenAndWatchAsync(
    ApiResult result,
    string token,
    string keyCredentialName,
    string secretCredentialName)
{
    if (string.IsNullOrWhiteSpace(token))
        throw new Exception("Token is missing.");

    if (token.Any(c => !char.IsLetterOrDigit(c)))
        throw new Exception(
            "Token contains invalid path characters."
        );

    string folder = Path.Combine(
        result.ClientFilePath,
        token
    );

    Directory.CreateDirectory(folder);

    string fileName =
        Path.GetFileName(result.FileName);

    string filePath =
        Path.Combine(
            folder,
            fileName
        );

    byte[] bytes =
        Convert.FromBase64String(
            result.FileData
        );

    File.WriteAllBytes(
        filePath,
        bytes
    );

    DateTime lastSentWriteTime =
        File.GetLastWriteTimeUtc(
            filePath
        );

    Process.Start(
        new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        }
    );

    await WaitUntilFileIsOpenedAsync(
        filePath
    );
           
File.WriteAllText(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt"
    ),
    ""
);

    int closedChecks = 0;

    while (closedChecks < 4)
    {
        await Task.Delay(500);

        if (!File.Exists(filePath))
        {
            closedChecks = 0;
            continue;
        }

        DateTime currentWriteTime =
            File.GetLastWriteTimeUtc(
                filePath
            );

        if (currentWriteTime != lastSentWriteTime)
        {
            await SendFileToApi(
                filePath,
                token,
                keyCredentialName,
                secretCredentialName
            );

            lastSentWriteTime =
                currentWriteTime;
        }

        if (CanOpenExclusive(filePath))
        {
            closedChecks++;
        }
        else
        {
            closedChecks = 0;
        }
    }

    DateTime finalWriteTime =
        File.GetLastWriteTimeUtc(
            filePath
        );

    if (finalWriteTime != lastSentWriteTime)
    {
        await SendFileToApi(
            filePath,
            token,
            keyCredentialName,
            secretCredentialName
        );
    }

    File.Delete(filePath);

    return filePath;
}


static async Task SendFileToApi(
    string filePath,
    string token,
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

    string environment =
        keyCredential.UserName
            .ToUpperInvariant();

    string accessKey =
        keyCredential.Password;

    string secretKey =
        secretCredential.Password;

    string url = environment switch
    {
        "DEV" =>
            "https://rhs-limsapou-83.ad.ous-hf.no/" +
            "STARLIMS.DEV/rest.web.api/v1/Folders/GetDocumentFromClient",

        _ => throw new Exception(
            $"Unknown environment: {environment}"
        )
    };

    string base64 =
        ReadFileAsBase64(
            filePath
        );

    string payload =
        JsonSerializer.Serialize(
            new
            {
                token,
                file = base64
            }
        );

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string stringToSign =
        $"{url}\n" +
        $"POST\n" +
        $"{accessKey}\n" +
        $"\n" +
        $"{timestamp}\n" +
        $"{payload}";

    string signature;

    using (HMACSHA256 hmac =
        new(
            Encoding.UTF8.GetBytes(
                secretKey
            )
        ))
    {
        signature =
            WebUtility.UrlEncode(
                Convert.ToBase64String(
                    hmac.ComputeHash(
                        Encoding.UTF8.GetBytes(
                            stringToSign
                        )
                    )
                )
            );
    }

    using HttpClientHandler handler =
        new()
        {
            UseDefaultCredentials = true
        };

    using HttpClient client =
        new(handler);

    using HttpRequestMessage request =
        new(
            HttpMethod.Post,
            url
        );

    byte[] payloadBytes =
        Encoding.UTF8.GetBytes(
            payload
        );

    request.Content =
        new ByteArrayContent(
            payloadBytes
        );

    request.Content.Headers.ContentType =
        new MediaTypeHeaderValue(
            "application/json"
        );

    request.Headers.TryAddWithoutValidation(
        "SL-API-Auth",
        accessKey
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
        await client.SendAsync(
            request
        );

    string body =
        await response.Content
            .ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"Upload API returned {(int)response.StatusCode}: {body}"
        );
    }
}


static string ReadFileAsBase64(
    string filePath)
{
    byte[] bytes;

    using (
        FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite |
            FileShare.Delete
        )
    )
    {
        bytes =
            new byte[stream.Length];

        int totalRead = 0;

        while (totalRead < bytes.Length)
        {
            int read =
                stream.Read(
                    bytes,
                    totalRead,
                    bytes.Length -
                    totalRead
                );

            if (read == 0)
                break;

            totalRead += read;
        }

        if (totalRead != bytes.Length)
        {
            throw new IOException(
                "Could not read complete file."
            );
        }
    }

    return Convert.ToBase64String(
        bytes
    );
}


static async Task WaitUntilFileIsOpenedAsync(
    string filePath)
{
    DateTime timeout =
        DateTime.UtcNow.AddSeconds(30);

    while (DateTime.UtcNow < timeout)
    {
        if (!CanOpenExclusive(filePath))
            return;

        await Task.Delay(250);
    }

    throw new Exception(
        "Could not detect that the file was opened."
    );
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


static async Task<ApiResult> CallApi(
    string token,
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

    string environment =
        keyCredential.UserName
            .ToUpperInvariant();

    string accessKey =
        keyCredential.Password;

    string secretKey =
        secretCredential.Password;

    string baseUrl = environment switch
    {
        "DEV" =>
            "https://rhs-limsapou-83.ad.ous-hf.no/" +
            "STARLIMS.DEV/rest.web.api/v1/Folders/SendDocumentToClient",

        _ => throw new Exception(
            $"Unknown environment: {environment}"
        )
    };

    string url =
        $"{baseUrl}?token={WebUtility.UrlEncode(token)}";

    string timestamp =
        DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

    string stringToSign =
        $"{url}\n" +
        $"GET\n" +
        $"{accessKey}\n" +
        $"\n" +
        $"{timestamp}\n" +
        $"\n";

    string signature;

    using (HMACSHA256 hmac =
        new(
            Encoding.UTF8.GetBytes(
                secretKey
            )
        ))
    {
        signature =
            WebUtility.UrlEncode(
                Convert.ToBase64String(
                    hmac.ComputeHash(
                        Encoding.UTF8.GetBytes(
                            stringToSign
                        )
                    )
                )
            );
    }

    using HttpClientHandler handler =
        new()
        {
            UseDefaultCredentials = true
        };

    using HttpClient client =
        new(handler);

    using HttpRequestMessage httpRequest =
        new(
            HttpMethod.Get,
            url
        );

    httpRequest.Content =
        new ByteArrayContent(
            Array.Empty<byte>()
        );

    httpRequest.Content.Headers.ContentType =
        new MediaTypeHeaderValue(
            "application/json"
        );

    httpRequest.Headers.TryAddWithoutValidation(
        "SL-API-Auth",
        accessKey
    );

    httpRequest.Headers.TryAddWithoutValidation(
        "SL-API-Timestamp",
        timestamp
    );

    httpRequest.Headers.TryAddWithoutValidation(
        "SL-API-Signature",
        signature
    );

    httpRequest.Headers.Accept.Add(
        new MediaTypeWithQualityHeaderValue(
            "application/json"
        )
    );

    using HttpResponseMessage response =
        await client.SendAsync(
            httpRequest
        );

    string body =
        await response.Content
            .ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        throw new Exception(
            $"API returned {(int)response.StatusCode}: {body}"
        );
    }

    ApiResponse apiResponse =
        JsonSerializer.Deserialize<ApiResponse>(
            body
        )
        ?? throw new Exception(
            "Invalid API response."
        );

    return apiResponse.Result
               .FirstOrDefault()
        ?? throw new Exception(
            "API returned no result."
        );
}


static string SaveAndOpen(
    ApiResult result)
{
    string filePath =
        Path.Combine(
            result.ClientFilePath,
            Path.GetFileName(
                result.FileName
            )
        );

    Directory.CreateDirectory(
        result.ClientFilePath
    );

    byte[] bytes =
        Convert.FromBase64String(
            result.FileData
        );

    File.WriteAllBytes(
        filePath,
        bytes
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


static Credential ReadCredential(
    string target)
{
    if (!CredReadW(
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
        CredFree(pointer);
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


[DllImport(
    "Advapi32.dll",
    EntryPoint = "CredReadW",
    CharSet = CharSet.Unicode,
    SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool CredReadW(
    string target,
    uint type,
    uint flags,
    out IntPtr credential
);


[DllImport(
    "Advapi32.dll",
    EntryPoint = "CredFree")]
static extern void CredFree(
    IntPtr buffer
);


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
    public string documentURL { get; set; } = "";

    [JsonPropertyName("FILE_DATA")]
    public string FileData { get; set; } = "";
}
