using System.IO;
using System.Security.Cryptography;

namespace WinCoreManager.Services.SelfUpdate;

internal static class ReleaseTrust
{
    // Base64-encoded DER SubjectPublicKeyInfo. Set only from the real publisher's offline key.
    internal static readonly string? PublisherPublicKey = null;
    internal const int SignatureSize = 64;

    public static void Verify(Stream release, Stream signature, string? publisherKey)
    {
        if (publisherKey is null || release.Length is <= 0 or > GitHubReleases.MaximumZipBytes
            || signature.Length != SignatureSize)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        var bytes = new byte[SignatureSize];
        signature.ReadExactly(bytes);
        try
        {
            var publicKey = Convert.FromBase64String(publisherKey);
            using var publisher = ECDsa.Create();
            publisher.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || publisher.KeySize != 256
                || publisher.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || !publisher.VerifyData(release, bytes, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new UpdateException(UpdateState.VerificationFailed, ex);
        }
    }
}
