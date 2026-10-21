// -----------------------------------------------------------------------
// <copyright file="MinisignVerifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using NSec.Cryptography;

namespace Netclaw.Configuration.Security;

/// <summary>
/// Verifies minisign Ed25519 signatures on binary feed manifests.
/// Minisign format: https://jedisct1.github.io/minisign/
/// </summary>
public static class MinisignVerifier
{
    // Minisign algorithm identifiers for Ed25519:
    // "ED" = standard (non-prehashed) detached signature — used by `minisign -S`
    // "Ed" = prehashed mode — used by `minisign -SH`
    // We accept both since the verification math is identical for Ed25519 pure.
    private static ReadOnlySpan<byte> Ed25519Standard => "ED"u8;
    private static ReadOnlySpan<byte> Ed25519Prehashed => "Ed"u8;

    // Embedded public key from feeds/releases/manifest.pub (fork feed key)
    // Decoded from: RWQGYXkcQwkBLtNMuo6nDyw83sExSt6qdJNPE1O9vEfRF5Mtl8AMD9zR
    // Format: 2-byte algorithm ("Ed") + 8-byte key ID + 32-byte Ed25519 public key
    private static ReadOnlySpan<byte> EmbeddedPublicKeyBlob => [
        0x45, 0x64, // "Ed" algorithm
        0x06, 0x61, 0x79, 0x1c, 0x43, 0x09, 0x01, 0x2e, // Key ID: 2E0109431C796106 (little-endian in file, display is big-endian)
        0xd3, 0x4c, 0xba, 0x8e, 0xa7, 0x0f, 0x2c, 0x3c, // Ed25519 public key (32 bytes)
        0xde, 0xc1, 0x31, 0x4a, 0xde, 0xaa, 0x74, 0x93,
        0x4f, 0x13, 0x53, 0xbd, 0xbc, 0x47, 0xd1, 0x17,
        0x93, 0x2d, 0x97, 0xc0, 0x0c, 0x0f, 0xdc, 0xd1,
    ];

    /// <summary>
    /// Test seam: when set, overrides the embedded public key for verification.
    /// Must be a 42-byte minisign public key blob (2 algo + 8 key ID + 32 key).
    /// Reset to null after tests to restore production behavior.
    /// </summary>
    internal static byte[]? TestPublicKeyOverride { get; set; }

    /// <summary>
    /// Test seam: forces <see cref="Verify"/> to return this result before any parsing
    /// or cryptographic work. Reset to null after tests.
    /// </summary>
    internal static VerifyResult? TestVerifyResultOverride { get; set; }

    /// <summary>
    /// The embedded Ed25519 public key ID (8 bytes) for matching against signatures.
    /// </summary>
    internal static ReadOnlySpan<byte> EmbeddedKeyId => EmbeddedPublicKeyBlob.Slice(2, 8);

    /// <summary>
    /// The embedded Ed25519 public key (32 bytes) for signature verification.
    /// </summary>
    internal static ReadOnlySpan<byte> EmbeddedPublicKey => EmbeddedPublicKeyBlob.Slice(10, 32);

    /// <summary>
    /// Result of a signature verification attempt.
    /// </summary>
    public enum VerifyResult
    {
        /// <summary>Signature is valid.</summary>
        Valid,

        /// <summary>Signature file format is malformed.</summary>
        MalformedSignature,

        /// <summary>Signature uses an unsupported algorithm (not Ed25519 pure).</summary>
        UnsupportedAlgorithm,

        /// <summary>Signature was produced by a different key than the embedded public key.</summary>
        KeyMismatch,

        /// <summary>Signature does not match the provided data.</summary>
        InvalidSignature,

        /// <summary>
        /// The cryptographic platform (NSec) failed to initialize.
        /// Non-fatal — callers should treat this as equivalent to a network error.
        /// </summary>
        PlatformUnavailable,
    }

    /// <summary>
    /// Parses a minisign signature file into its components.
    /// Format: line 1 = "untrusted comment: ...", line 2 = base64(2-byte algo + 8-byte key ID + 64-byte signature).
    /// </summary>
    /// <returns>The parsed signature, or null if the format is invalid.</returns>
    internal static ParsedSignature? ParseSignature(string signatureFileContent)
    {
        var lines = signatureFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return null;

        // Line 1 must start with "untrusted comment:"
        if (!lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal))
            return null;

        // Line 2 is base64-encoded signature blob
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(lines[1].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        // Expected: 2 (algo) + 8 (key ID) + 64 (Ed25519 signature) = 74 bytes
        if (blob.Length != 74)
            return null;

        return new ParsedSignature
        {
            Algorithm = blob.AsSpan(0, 2).ToArray(),
            KeyId = blob.AsSpan(2, 8).ToArray(),
            Signature = blob.AsSpan(10, 64).ToArray(),
        };
    }

    /// <summary>
    /// Parses a minisign public key file into its components.
    /// Format: line 1 = "untrusted comment: ...", line 2 = base64(2-byte algo + 8-byte key ID + 32-byte key).
    /// </summary>
    internal static ParsedPublicKey? ParsePublicKey(string publicKeyFileContent)
    {
        var lines = publicKeyFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(lines[1].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        // Expected: 2 (algo) + 8 (key ID) + 32 (Ed25519 public key) = 42 bytes
        if (blob.Length != 42)
            return null;

        return new ParsedPublicKey
        {
            Algorithm = blob.AsSpan(0, 2).ToArray(),
            KeyId = blob.AsSpan(2, 8).ToArray(),
            Key = blob.AsSpan(10, 32).ToArray(),
        };
    }

    /// <summary>
    /// Verifies a minisign signature against data using the embedded public key.
    /// </summary>
    /// <param name="data">The signed data (manifest content).</param>
    /// <param name="signatureFileContent">The raw content of the .sig file.</param>
    /// <returns>The verification result.</returns>
    public static VerifyResult Verify(ReadOnlySpan<byte> data, string signatureFileContent)
    {
        if (TestVerifyResultOverride is { } overrideResult)
            return overrideResult;

        var sig = ParseSignature(signatureFileContent);
        if (sig is null)
            return VerifyResult.MalformedSignature;

        // Check algorithm is Ed25519 (standard or prehashed)
        if (!sig.Algorithm.AsSpan().SequenceEqual(Ed25519Standard) &&
            !sig.Algorithm.AsSpan().SequenceEqual(Ed25519Prehashed))
            return VerifyResult.UnsupportedAlgorithm;

        // Use test override if set, otherwise embedded production key
        ReadOnlySpan<byte> activeKeyId;
        ReadOnlySpan<byte> activePublicKey;
        if (TestPublicKeyOverride is { Length: 42 } testBlob)
        {
            activeKeyId = testBlob.AsSpan(2, 8);
            activePublicKey = testBlob.AsSpan(10, 32);
        }
        else
        {
            activeKeyId = EmbeddedKeyId;
            activePublicKey = EmbeddedPublicKey;
        }

        // Check key ID matches active key
        if (!sig.KeyId.AsSpan().SequenceEqual(activeKeyId))
            return VerifyResult.KeyMismatch;

        // Try raw Ed25519 first (works with test keys and legacy minisign).
        var (rawSuccess, rawEarlyReturn) = TryVerifyEd25519(activePublicKey, data, sig.Signature);
        if (rawEarlyReturn is not null)
            return rawEarlyReturn.Value;
        if (rawSuccess)
            return VerifyResult.Valid;

        // Modern minisign (1.0.18+) always prehashes with BLAKE2b-512 before
        // signing, even when algorithm bytes say "ED" (nominally "standard").
        // Fall back to prehashed verification.
        Span<byte> hash = stackalloc byte[64];
        Blake2b512(data, hash);

        var (hashSuccess, hashEarlyReturn) = TryVerifyEd25519(activePublicKey, hash, sig.Signature);
        if (hashEarlyReturn is not null)
            return hashEarlyReturn.Value;
        return hashSuccess ? VerifyResult.Valid : VerifyResult.InvalidSignature;
    }

    private static (bool success, VerifyResult? earlyReturn) TryVerifyEd25519(
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            return (VerifyEd25519(publicKey, data, signature), null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PlatformNotSupportedException)
        {
            return (false, VerifyResult.PlatformUnavailable);
        }
    }

    /// <summary>
    /// Verifies an Ed25519 signature using NSec.Cryptography.
    /// </summary>
    internal static bool VerifyEd25519(
        ReadOnlySpan<byte> publicKeyBytes,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signatureBytes)
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
        return algorithm.Verify(publicKey, data, signatureBytes);
    }

    /// <summary>
    /// Computes unkeyed BLAKE2b-512 (64-byte output) using Blake2Fast.
    /// This is the same hash minisign uses for prehashing before Ed25519 signing.
    /// </summary>
    private static void Blake2b512(ReadOnlySpan<byte> data, Span<byte> hash)
    {
        Blake2Fast.Blake2b.ComputeAndWriteHash(64, data, hash);
    }

    internal sealed class ParsedSignature
    {
        public required byte[] Algorithm { get; init; }
        public required byte[] KeyId { get; init; }
        public required byte[] Signature { get; init; }
    }

    internal sealed class ParsedPublicKey
    {
        public required byte[] Algorithm { get; init; }
        public required byte[] KeyId { get; init; }
        public required byte[] Key { get; init; }
    }
}
