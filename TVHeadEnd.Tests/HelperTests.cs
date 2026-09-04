using System.Security.Cryptography;
using System.Text;
using TVHeadEnd.Helper;

namespace TVHeadEnd.Tests;

/// <summary>Small pure helpers that the login and the timer scheduling depend on.</summary>
public class HelperTests
{
    [Fact]
    public void SaltedSha1_MatchesPasswordFollowedBySalt()
    {
        byte[] salt = { 1, 2, 3, 4, 5 };

        byte[] actual = SHA1helper.GenerateSaltedSHA1("secret", salt);

        byte[] expected = SHA1.HashData(Encoding.UTF8.GetBytes("secret").Concat(salt).ToArray());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaltedSha1_IsDeterministic()
    {
        byte[] salt = { 9, 8, 7 };

        Assert.Equal(
            SHA1helper.GenerateSaltedSHA1("secret", salt),
            SHA1helper.GenerateSaltedSHA1("secret", salt));
    }

    [Fact]
    public void SaltedSha1_ChangesWithTheSalt()
    {
        Assert.NotEqual(
            SHA1helper.GenerateSaltedSHA1("secret", new byte[] { 1 }),
            SHA1helper.GenerateSaltedSHA1("secret", new byte[] { 2 }));
    }

    [Fact]
    public void SaltedSha1_AcceptsAnEmptyChallenge()
    {
        // TVHeadend may answer the hello without a challenge field.
        byte[] digest = SHA1helper.GenerateSaltedSHA1("secret", Array.Empty<byte>());

        Assert.Equal(20, digest.Length);
    }

    [Fact]
    public void SaltedSha1_HandlesNonAsciiPasswords()
    {
        byte[] digest = SHA1helper.GenerateSaltedSHA1("Paßwort-ü", new byte[] { 1, 2 });

        byte[] expected = SHA1.HashData(
            Encoding.UTF8.GetBytes("Paßwort-ü").Concat(new byte[] { 1, 2 }).ToArray());
        Assert.Equal(expected, digest);
    }

    [Fact]
    public void UnixTime_OfTheEpochIsZero()
    {
        DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, DateTimeHelper.getUnixUTCTimeFromUtcDateTime(epoch));
    }

    [Fact]
    public void UnixTime_MatchesADateTimeOffset()
    {
        DateTime moment = new(2026, 9, 4, 20, 15, 0, DateTimeKind.Utc);

        Assert.Equal(
            ((DateTimeOffset)moment).ToUnixTimeSeconds(),
            DateTimeHelper.getUnixUTCTimeFromUtcDateTime(moment));
    }

    [Fact]
    public void UnixTime_HandlesDatesBeyond2038()
    {
        DateTime moment = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            ((DateTimeOffset)moment).ToUnixTimeSeconds(),
            DateTimeHelper.getUnixUTCTimeFromUtcDateTime(moment));
    }
}
