using System.Security.Cryptography;

namespace TelegramToMatrixForward.Storage;

/// <summary>
/// Хранение связей с AES-256-GCM шифрованием.
/// </summary>
internal sealed class EncryptedLinkStore
{
    private const uint Magic = 0x_544C_4E4B; // "TLNK. Идентификатор файла"
    private const uint Version = 1;

    private readonly byte[] _encryptionKey;
    private readonly string _filePath;

    public EncryptedLinkStore(string filePath, string encryptionKey)
    {
        _filePath = filePath;
        _encryptionKey = System.Text.Encoding.UTF8.GetBytes(encryptionKey);
#pragma warning disable MEN010 // Длина ключа 32 байта.
        if (_encryptionKey.Length != 32)
        {
            throw new ArgumentException("Encryption key must be 32 bytes (256 bits).");
        }
#pragma warning restore MEN010
    }

    /// <summary>
    /// Загружает и расшифровывает связи из файла.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task<Dictionary<long, string>> LoadAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, string>();

        if (File.Exists(_filePath))
        {
            var encryptedData = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);

            if (encryptedData.Length > AesGcm.NonceByteSizes.MaxSize)
            {
                var iv = new byte[AesGcm.NonceByteSizes.MaxSize];
                Array.Copy(encryptedData, 0, iv, 0, iv.Length);

                var cipherText = new byte[encryptedData.Length - iv.Length];
                Array.Copy(encryptedData, iv.Length, cipherText, 0, cipherText.Length);

                var decryptedData = DecryptAesGcm(cipherText, iv);
                result = ParseBinaryData(decryptedData);
            }
        }

        return result;
    }

    /// <summary>
    /// Шифрует и сохраняет связи в файл.
    /// </summary>
    /// <param name="links">Массив ссылок.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    public async Task SaveAsync(Dictionary<long, string> links, CancellationToken cancellationToken)
    {
        var binaryData = SerializeToBinary(links);
        var iv = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var encryptedData = EncryptAesGcm(binaryData, iv);

        var fileData = new byte[AesGcm.NonceByteSizes.MaxSize + encryptedData.Length];
        Array.Copy(iv, 0, fileData, 0, AesGcm.NonceByteSizes.MaxSize);
        Array.Copy(encryptedData, 0, fileData, AesGcm.NonceByteSizes.MaxSize, encryptedData.Length);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(_filePath, fileData, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeToBinary(Dictionary<long, string> links)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(links.Count);

        foreach (var (telegramId, matrixId) in links)
        {
            writer.Write(telegramId);
            var matrixIdBytes = System.Text.Encoding.UTF8.GetBytes(matrixId);
            writer.Write(matrixIdBytes.Length);
            writer.Write(matrixIdBytes);
            writer.Write(DateTime.UtcNow.Ticks);
        }

        return ms.ToArray();
    }

    private static Dictionary<long, string> ParseBinaryData(byte[] data)
    {
        var result = new Dictionary<long, string>();

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException($"Invalid magic: {magic:X8}");
        }

        var version = reader.ReadUInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported version: {version}");
        }

        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var telegramId = reader.ReadInt64();
            var matrixIdLength = reader.ReadInt32();
            var matrixIdBytes = reader.ReadBytes(matrixIdLength);
            var matrixId = System.Text.Encoding.UTF8.GetString(matrixIdBytes);
            var createdAtTicks = reader.ReadInt64(); // пока не используем

            result[telegramId] = matrixId;
        }

        return result;
    }

    private byte[] EncryptAesGcm(byte[] data, byte[] iv)
    {
        using var aes = new AesGcm(_encryptionKey, AesGcm.TagByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var encryptedData = new byte[data.Length];

        aes.Encrypt(iv, data, encryptedData, tag);

        var result = new byte[encryptedData.Length + tag.Length];
        Array.Copy(encryptedData, 0, result, 0, encryptedData.Length);
        Array.Copy(tag, 0, result, encryptedData.Length, tag.Length);

        return result;
    }

    private byte[] DecryptAesGcm(byte[] cipherText, byte[] iv)
    {
        using var aes = new AesGcm(_encryptionKey, AesGcm.TagByteSizes.MaxSize);

        var tagLength = AesGcm.TagByteSizes.MaxSize;
        var encryptedDataLength = cipherText.Length - tagLength;

        var encryptedData = new byte[encryptedDataLength];
        var tag = new byte[tagLength];

        Array.Copy(cipherText, 0, encryptedData, 0, encryptedDataLength);
        Array.Copy(cipherText, encryptedDataLength, tag, 0, tagLength);

        var decryptedData = new byte[encryptedDataLength];
        aes.Decrypt(iv, encryptedData, tag, decryptedData);

        return decryptedData;
    }
}
