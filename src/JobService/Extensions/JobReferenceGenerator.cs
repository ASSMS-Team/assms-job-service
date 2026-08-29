using System.Security.Cryptography;

namespace JobService.Extensions;

// Builds the human-readable job reference: JOB- followed by six characters
// drawn from a 32-character alphabet.
public static class JobReferenceGenerator
{
    public const string Prefix = "JOB-";

    public const int RandomLength = 6;

    // Crockford's base32: the ten digits and the twenty-six letters, less I, L,
    // O and U. I and L are dropped because they are read back as 1, O because
    // it is read back as 0, and U so that six random characters cannot spell
    // something the Agent would rather not read down the phone. Thirty-six less
    // four is thirty-two.
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // JOB- plus six.
    public const int ReferenceLength = 10;

    // Random, not sequential: a sequential reference would leak how many jobs
    // the business has taken, and the unique index plus the caller's retry is
    // what makes a collision a non-event rather than a failure.
    //
    // RandomNumberGenerator rather than Random: the value ends up in front of
    // customers as an identifier, and a predictable one lets a caller guess
    // other people's references. GetInt32 draws uniformly over the range, so no
    // character of the alphabet is more likely than another.
    public static string Generate()
    {
        var characters = new char[RandomLength];

        for (var i = 0; i < RandomLength; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Prefix + new string(characters);
    }
}
