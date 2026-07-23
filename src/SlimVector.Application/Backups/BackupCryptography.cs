using System.Security.Cryptography;
using MemoryPack;

namespace SlimVector.Application.Backups;

internal static class BackupCryptography
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = "SlimVector.Backup.v1"u8.ToArray();

    public static byte[] Pack(ReadOnlySpan<byte> plaintext, byte[]? key)
    {
        BackupEnvelope envelope;
        if (key is null)
        {
            envelope = new BackupEnvelope { Data = plaintext.ToArray() };
        }
        else
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];
            using AesGcm cipher = new(key, TagSize);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            envelope = new BackupEnvelope
            {
                Encrypted = true,
                Nonce = nonce,
                Tag = tag,
                Data = ciphertext,
            };
        }

        return MemoryPackSerializer.Serialize(envelope);
    }

    public static byte[] Unpack(ReadOnlySpan<byte> packed, byte[]? key)
    {
        BackupEnvelope envelope = MemoryPackSerializer.Deserialize<BackupEnvelope>(packed)
            ?? throw new InvalidDataException("The backup envelope is empty.");
        if (envelope.FormatVersion != 1)
        {
            throw new InvalidDataException($"Backup envelope version '{envelope.FormatVersion}' is unsupported.");
        }

        if (!envelope.Encrypted)
        {
            return envelope.Data;
        }

        if (key is null)
        {
            throw new InvalidDataException("The backup is encrypted but no encryption key is configured.");
        }

        if (envelope.Nonce.Length != NonceSize || envelope.Tag.Length != TagSize)
        {
            throw new InvalidDataException("The encrypted backup envelope has invalid nonce or tag lengths.");
        }

        byte[] plaintext = new byte[envelope.Data.Length];
        using AesGcm cipher = new(key, TagSize);
        try
        {
            cipher.Decrypt(envelope.Nonce, envelope.Data, envelope.Tag, plaintext, AssociatedData);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new InvalidDataException("Backup authentication failed.", exception);
        }

        return plaintext;
    }
}
