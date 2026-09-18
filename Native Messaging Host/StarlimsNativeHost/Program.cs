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

        ApiResult result = await CallApi(
            request.Token,
            request.Key,
            request.Secret
        );

        if (result.FileAction == "readwrite")
        {
            string filePath = SaveAndOpen(result);

            WriteResponse(output, new
            {
                ok = true,
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

static async Task<ApiResult> CallApi(
    string token,
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
        new(Encoding.UTF8.GetBytes(secretKey)))
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

    using HttpClientHandler handler = new()
    {
        UseDefaultCredentials = true
    };

    using HttpClient client =
        new(handler);

    using HttpRequestMessage httpRequest =
        new(HttpMethod.Get, url);

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
        await client.SendAsync(httpRequest);

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

    output.Write(
        length
    );

    output.Write(
        data
    );

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

    [JsonPropertyName("FILE_DATA")]
    public string FileData { get; set; } = "";
}
