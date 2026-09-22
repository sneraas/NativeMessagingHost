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

class Program
{
    static readonly HttpClient Client = CreateHttpClient();

    static async Task Main()
    {
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

                NativeRequest nativeRequest =
                    JsonSerializer.Deserialize<NativeRequest>(json)
                    ?? throw new Exception("Invalid request.");

                ApiContext api = GetApiContext(
                    nativeRequest.Key,
                    nativeRequest.Secret
                );

                ApiResult result = await GetDocumentInfo(
                    nativeRequest.Token,
                    api
                );

                Stamp("02_metadata_received");

                byte[] fileBytes = await DownloadFile(
                    result.DownloadUrl,
                    api
                );

                Stamp("03_binary_downloaded");

                string filePath = SaveFile(
                    result,
                    nativeRequest.Token,
                    fileBytes
                );

                Stamp("04_file_saved");

                OpenFile(filePath);

                Stamp("05_process_started");

                if (result.FileAction == "readwrite")
                {
                    await WatchFile(
                        filePath,
                        nativeRequest.Token,
                        api
                    );
                }
                else if (result.FileAction != "read")
                {
                    throw new Exception(
                        $"Unsupported FILE_ACTION: {result.FileAction}"
                    );
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
                WriteError(ex);

                WriteResponse(output, new
                {
                    ok = false,
                    error = ex.Message
                });
            }
        }
    }


    static async Task<ApiResult> GetDocumentInfo(
        string token,
        ApiContext api)
    {
        string url =
            api.ApiRoot +
            "v1/Folders/SendDocumentToClient" +
            $"?token={WebUtility.UrlEncode(token)}";

        using HttpRequestMessage request =
            CreateSignedRequest(
                HttpMethod.Get,
                url,
                "application/json",
                "",
                api
            );

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"
            )
        );

        using HttpResponseMessage response =
            await Client.SendAsync(request);

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
        ApiContext api)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception("URL is empty.");

        using HttpRequestMessage request =
            CreateSignedRequest(
                HttpMethod.Get,
                url,
                "text/plain",
                "",
                api
            );

        using HttpResponseMessage response =
            await Client.SendAsync(request);

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
        else
        {
            folder =
                result.ClientFilePath;
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

        return filePath;
    }


    static void OpenFile(
        string filePath)
    {
        Process.Start(
            new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            }
        );
    }


    static async Task WatchFile(
        string filePath,
        string token,
        ApiContext api)
    {
        DateTime lastSentWriteTime =
            File.GetLastWriteTimeUtc(
                filePath
            );

        await WaitUntilFileIsOpened(
            filePath
        );

        Stamp("06_file_opened");

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

            if (currentWriteTime !=
                lastSentWriteTime)
            {
                try
                {
                    await SendFileToApi(
                        filePath,
                        token,
                        api
                    );

                    lastSentWriteTime =
                        currentWriteTime;

                    Stamp("07_save_uploaded");
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
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

        Stamp("08_file_closed");

        DateTime finalWriteTime =
            File.GetLastWriteTimeUtc(
                filePath
            );

        if (finalWriteTime !=
            lastSentWriteTime)
        {
            await SendFileToApi(
                filePath,
                token,
                api
            );

            Stamp("09_final_upload");
        }

        File.Delete(filePath);

        Stamp("10_local_file_deleted");
    }


    static async Task SendFileToApi(
        string filePath,
        string token,
        ApiContext api)
    {
        byte[] bytes =
            ReadFileBytes(
                filePath
            );

        string base64 =
            Convert.ToBase64String(
                bytes
            );

        string payload =
            JsonSerializer.Serialize(
                new
                {
                    token,
                    file = base64
                }
            );

        string url =
            api.ApiRoot +
            "v1/Folders/GetDocumentFromClient";

        using HttpRequestMessage request =
            CreateSignedRequest(
                HttpMethod.Post,
                url,
                "application/json",
                payload,
                api
            );

        using HttpResponseMessage response =
            await Client.SendAsync(request);

        string body =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(
                $"Upload API returned {(int)response.StatusCode}: {body}"
            );
        }
    }


    static byte[] ReadFileBytes(
        string filePath)
    {
        using FileStream stream =
            new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete
            );

        using MemoryStream memory =
            new();

        stream.CopyTo(memory);

        return memory.ToArray();
    }


    static async Task WaitUntilFileIsOpened(
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


    static HttpRequestMessage CreateSignedRequest(
        HttpMethod method,
        string url,
        string contentType,
        string payload,
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
                method.Method,
                api.AccessKey,
                timestamp,
                payload,
                api.SecretKey
            );

        HttpRequestMessage request =
            new(
                method,
                url
            );

        byte[] content =
            Encoding.UTF8.GetBytes(
                payload
            );

        request.Content =
            new ByteArrayContent(
                content
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
            Convert.ToBase64String(
                hash
            )
        );
    }


    static ApiContext GetApiContext(
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

        string apiRoot =
            environment switch
            {
                "DEV" =>
                    "https://rhs-limsapou-83.ad.ous-hf.no/" +
                    "STARLIMS.DEV/rest.web.api/",

                _ => throw new Exception(
                    $"Unknown environment: {environment}"
                )
            };

        return new ApiContext(
            apiRoot,
            keyCredential.Password,
            secretCredential.Password
        );
    }


    static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler =
            new()
            {
                UseDefaultCredentials = true
            };

        return new HttpClient(
            handler
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
            NativeMethods.CredFree(
                pointer
            );
        }
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


    static void WriteError(
        Exception ex)
    {
        string fileName =
            $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_ERROR.txt";

        File.WriteAllText(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments
                ),
                fileName
            ),
            ex.ToString()
        );
    }


    static string? ReadMessage(
        Stream input)
    {
        byte[] lengthBytes =
            new byte[4];

        int lengthRead =
            ReadExact(
                input,
                lengthBytes,
                4
            );

        if (lengthRead == 0)
            return null;

        if (lengthRead != 4)
        {
            throw new Exception(
                "Invalid native message length."
            );
        }

        int length =
            BitConverter.ToInt32(
                lengthBytes,
                0
            );

        byte[] data =
            new byte[length];

        int dataRead =
            ReadExact(
                input,
                data,
                length
            );

        if (dataRead != length)
        {
            throw new Exception(
                "Incomplete native message."
            );
        }

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
