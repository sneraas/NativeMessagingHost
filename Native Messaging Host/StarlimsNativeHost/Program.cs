using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

internal static class Program
{
    private const int MaxIncomingMessageSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static async Task Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        while (true)
        {
            try
            {
                string? json = ReadNativeMessage(input);

                if (json is null)
                    break;

                NativeRequest request =
                    JsonSerializer.Deserialize<NativeRequest>(
                        json,
                        JsonOptions
                    )
                    ?? throw new InvalidOperationException(
                        "Invalid request."
                    );

                ValidateRequest(request);

                ApiFileResult result =
                    await CallStarlimsApiAsync(request);

                string? filePath = HandleApiResult(result);

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
    }

    private static void ValidateRequest(NativeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new InvalidOperationException(
                "Token is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            throw new InvalidOperationException(
                "API key credential target is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(request.Secret))
        {
            throw new InvalidOperationException(
                "API secret credential target is missing."
            );
        }
    }

    private static async Task<ApiFileResult> CallStarlimsApiAsync(
        NativeRequest nativeRequest)
    {
        string environment = GetEnvironmentFromCredentialTargets(
            nativeRequest.Key,
            nativeRequest.Secret
        );

        CredentialEntry keyCredential =
            CredentialManager.ReadGeneric(
                nativeRequest.Key
            );

        CredentialEntry secretCredential =
            CredentialManager.ReadGeneric(
                nativeRequest.Secret
            );

        /*
         * Both credentials must belong to the same environment.
         *
         * Example:
         *
         * DEV_API_KEY
         * Username: DEV
         *
         * DEV_API_SECRET
         * Username: DEV
         */

        if (!string.Equals(
                keyCredential.UserName,
                environment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Credential '{nativeRequest.Key}' " +
                $"does not belong to environment '{environment}'."
            );
        }

        if (!string.Equals(
                secretCredential.UserName,
                environment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Credential '{nativeRequest.Secret}' " +
                $"does not belong to environment '{environment}'."
            );
        }

        string accessKey = keyCredential.Password;
        string secretKey = secretCredential.Password;

        if (string.IsNullOrEmpty(accessKey))
        {
            throw new UnauthorizedAccessException(
                "API key credential contains no password."
            );
        }

        if (string.IsNullOrEmpty(secretKey))
        {
            throw new UnauthorizedAccessException(
                "API secret credential contains no password."
            );
        }

        string apiUrl = GetApiUrl(environment);

        string url =
            $"{apiUrl}?token=" +
            Uri.EscapeDataString(nativeRequest.Token);

        const string method = "GET";
        const string apiVerb = "";
        const string body = "";

        string timestamp = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture
        );

        /*
         * Must match STARLIMS signing format exactly:
         *
         * URL
         * GET
         * AccessKey
         * ApiVerb
         * Timestamp
         * Body
         */

        string stringToSign =
            $"{url}\n" +
            $"{method}\n" +
            $"{accessKey}\n" +
            $"{apiVerb}\n" +
            $"{timestamp}\n" +
            $"{body}";

        string signature = CreateSignature(
            stringToSign,
            secretKey
        );

        using HttpClientHandler handler = new()
        {
            UseDefaultCredentials = true,
            PreAuthenticate = true
        };

        using HttpClient client = new(handler);

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            url
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

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"
            )
        );

        using HttpResponseMessage response =
            await client.SendAsync(request);

        string responseBody =
            await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"STARLIMS API returned " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. " +
                $"{responseBody}"
            );
        }

        ApiResponse? apiResponse =
            JsonSerializer.Deserialize<ApiResponse>(
                responseBody,
                JsonOptions
            );

        if (apiResponse?.Result is null ||
            apiResponse.Result.Count == 0)
        {
            throw new InvalidOperationException(
                "STARLIMS API returned no result."
            );
        }

        return apiResponse.Result[0];
    }

    private static string CreateSignature(
        string stringToSign,
        string secretKey)
    {
        byte[] keyBytes =
            Encoding.UTF8.GetBytes(secretKey);

        byte[] dataBytes =
            Encoding.UTF8.GetBytes(stringToSign);

        byte[] hashBytes;

        using (HMACSHA256 hmac = new(keyBytes))
        {
            hashBytes =
                hmac.ComputeHash(dataBytes);
        }

        string signatureRaw =
            Convert.ToBase64String(hashBytes);

        /*
         * PowerShell used:
         *
         * HttpUtility.UrlEncode(signatureRaw)
         *
         * Base64 only contains a limited set of characters
         * requiring escaping (+ / =), so EscapeDataString
         * gives us the required representation here.
         */

        return Uri.EscapeDataString(signatureRaw);
    }

    private static string GetEnvironmentFromCredentialTargets(
        string keyTarget,
        string secretTarget)
    {
        const string keySuffix = "_API_KEY";
        const string secretSuffix = "_API_SECRET";

        if (!keyTarget.EndsWith(
                keySuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Invalid API key credential target."
            );
        }

        if (!secretTarget.EndsWith(
                secretSuffix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Invalid API secret credential target."
            );
        }

        string keyEnvironment =
            keyTarget[..^keySuffix.Length];

        string secretEnvironment =
            secretTarget[..^secretSuffix.Length];

        if (string.IsNullOrWhiteSpace(keyEnvironment))
        {
            throw new UnauthorizedAccessException(
                "Credential environment is missing."
            );
        }

        if (!string.Equals(
                keyEnvironment,
                secretEnvironment,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "API key and API secret belong " +
                "to different environments."
            );
        }

        return keyEnvironment.ToUpperInvariant();
    }

    private static string GetApiUrl(
        string environment)
    {
        /*
         * The extension does NOT control the URL.
         *
         * The environment obtained from the credentials
         * determines which STARLIMS API may be contacted.
         */

        return environment switch
        {
            "DEV" =>
                "https://rhs-limsapou-83.ad.ous-hf.no/" +
                "STARLIMS.DEV/rest.web.api/v1/" +
                "Folders/SendDocumentToClient",

            /*
             * Add PROD later:
             *
             * "PROD" =>
             *     "https://production-server/" +
             *     "STARLIMS/rest.web.api/v1/" +
             *     "Folders/SendDocumentToClient",
             */

            _ => throw new UnauthorizedAccessException(
                $"Unknown STARLIMS environment: {environment}"
            )
        };
    }

    private static string? HandleApiResult(
        ApiFileResult result)
    {
        if (string.IsNullOrWhiteSpace(
                result.FileAction))
        {
            throw new InvalidOperationException(
                "STARLIMS API returned no FILE_ACTION."
            );
        }

        /*
         * FILE_ACTION comes from STARLIMS.
         *
         * Nothing from the browser decides what the
         * native host does with the returned file.
         */

        switch (result.FileAction.ToLowerInvariant())
        {
            case "readwrite":
                return SaveAndOpenFile(result);

            default:
                throw new InvalidOperationException(
                    $"Unsupported FILE_ACTION: " +
                    $"{result.FileAction}"
                );
        }
    }

    private static string SaveAndOpenFile(
        ApiFileResult result)
    {
        if (string.IsNullOrWhiteSpace(
                result.ClientFilePath))
        {
            throw new InvalidOperationException(
                "CLIENT_FILE_PATH is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(
                result.FileName))
        {
            throw new InvalidOperationException(
                "FILE_NAME is missing."
            );
        }

        if (string.IsNullOrWhiteSpace(
                result.FileData))
        {
            throw new InvalidOperationException(
                "FILE_DATA is missing."
            );
        }

        /*
         * FILE_NAME must only be a filename.
         * This prevents FILE_NAME itself from injecting
         * another directory.
         */

        string fileName =
            Path.GetFileName(result.FileName);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException(
                "Invalid FILE_NAME."
            );
        }

        string directory =
            Path.GetFullPath(
                result.ClientFilePath
            );

        Directory.CreateDirectory(directory);

        string filePath =
            Path.Combine(
                directory,
                fileName
            );

        byte[] fileBytes;

        try
        {
            fileBytes =
                Convert.FromBase64String(
                    result.FileData
                );
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "FILE_DATA is not valid Base64.",
                ex
            );
        }

        File.WriteAllBytes(
            filePath,
            fileBytes
        );

        Process? process =
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                }
            );

        if (process is null)
        {
            throw new InvalidOperationException(
                $"Could not open file: {filePath}"
            );
        }

        return filePath;
    }

    private static string? ReadNativeMessage(
        Stream input)
    {
        byte[] lengthBytes = new byte[4];

        int lengthRead =
            ReadExact(
                input,
                lengthBytes,
                0,
                4
            );

        if (lengthRead == 0)
            return null;

        if (lengthRead != 4)
        {
            throw new EndOfStreamException(
                "Incomplete native messaging header."
            );
        }

        int length =
            BitConverter.ToInt32(
                lengthBytes,
                0
            );

        if (length <= 0)
        {
            throw new InvalidOperationException(
                "Invalid native messaging message length."
            );
        }

        if (length > MaxIncomingMessageSize)
        {
            throw new InvalidOperationException(
                $"Native messaging message is too large: " +
                $"{length} bytes."
            );
        }

        byte[] buffer =
            new byte[length];

        int bodyRead =
            ReadExact(
                input,
                buffer,
                0,
                length
            );

        if (bodyRead != length)
        {
            throw new EndOfStreamException(
                "Incomplete native messaging message."
            );
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static int ReadExact(
        Stream stream,
        byte[] buffer,
        int offset,
        int count)
    {
        int totalRead = 0;

        while (totalRead < count)
        {
            int read =
                stream.Read(
                    buffer,
                    offset + totalRead,
                    count - totalRead
                );

            if (read == 0)
                return totalRead;

            totalRead += read;
        }

        return totalRead;
    }

    private static void WriteResponse(
        Stream output,
        object response)
    {
        string json =
            JsonSerializer.Serialize(response);

        byte[] bytes =
            Encoding.UTF8.GetBytes(json);

        byte[] length =
            BitConverter.GetBytes(bytes.Length);

        output.Write(
            length,
            0,
            length.Length
        );

        output.Write(
            bytes,
            0,
            bytes.Length
        );

        output.Flush();
    }
}

internal sealed class NativeRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
}

internal sealed class ApiResponse
{
    [JsonPropertyName("Result")]
    public List<ApiFileResult>? Result { get; set; }
}

internal sealed class ApiFileResult
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

internal sealed record CredentialEntry(
    string UserName,
    string Password
);

internal static class CredentialManager
{
    private const uint CredTypeGeneric = 1;

    public static CredentialEntry ReadGeneric(
        string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException(
                "Credential target is empty.",
                nameof(target)
            );
        }

        bool success =
            CredReadW(
                target,
                CredTypeGeneric,
                0,
                out IntPtr credentialPointer
            );

        if (!success)
        {
            int error =
                Marshal.GetLastWin32Error();

            throw new Win32Exception(
                error,
                $"Generic credential '{target}' " +
                "was not found or could not be read."
            );
        }

        try
        {
            NativeCredential credential =
                Marshal.PtrToStructure<NativeCredential>(
                    credentialPointer
                );

            string userName =
                Marshal.PtrToStringUni(
                    credential.UserName
                )
                ?? "";

            string password = "";

            if (credential.CredentialBlob != IntPtr.Zero &&
                credential.CredentialBlobSize > 0)
            {
                int blobSize =
                    checked(
                        (int)credential.CredentialBlobSize
                    );

                byte[] passwordBytes =
                    new byte[blobSize];

                Marshal.Copy(
                    credential.CredentialBlob,
                    passwordBytes,
                    0,
                    blobSize
                );

                password =
                    Encoding.Unicode
                        .GetString(passwordBytes)
                        .TrimEnd('\0');

                /*
                 * Clear our managed temporary copy.
                 * The resulting string itself is immutable,
                 * but this at least removes the duplicate
                 * byte array as soon as possible.
                 */

                CryptographicOperations.ZeroMemory(
                    passwordBytes
                );
            }

            return new CredentialEntry(
                userName,
                password
            );
        }
        finally
        {
            CredFree(
                credentialPointer
            );
        }
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(
        string target,
        uint type,
        uint flags,
        out IntPtr credential
    );

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredFree",
        SetLastError = true)]
    private static extern void CredFree(
        IntPtr buffer
    );

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct NativeCredential
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
}
