using System.Text.RegularExpressions;

using JobService.Extensions;

namespace JobService.Tests;

public class JobReferenceGeneratorTests
{
    // JOB- then six characters from the alphabet. The class spells out the
    // alphabet rather than using \w so that a character the generator should
    // never emit fails the match instead of passing it.
    private static readonly Regex ReferencePattern =
        new("^JOB-[0-9A-HJKMNP-TV-Z]{6}$", RegexOptions.Compiled);

    // I and L read back as 1, O reads back as 0, and U is dropped so six random
    // characters cannot spell something an Agent would rather not read out.
    private const string ExcludedCharacters = "ILOU";

    // One draw proves nothing about a random generator, so the format tests run
    // over a sample big enough that a rare bad character would show up.
    private const int SampleSize = 2000;

    [Fact]
    public void Generate_ReturnsPrefixedSixCharacterReference()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            var reference = JobReferenceGenerator.Generate();

            Assert.Matches(ReferencePattern, reference);
        }
    }

    [Fact]
    public void Generate_ReturnsTenCharacters()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            var reference = JobReferenceGenerator.Generate();

            // Four for "JOB-" and six random.
            Assert.Equal(10, reference.Length);
            Assert.Equal(JobReferenceGenerator.ReferenceLength, reference.Length);
            Assert.StartsWith(JobReferenceGenerator.Prefix, reference, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generate_NeverEmitsAnExcludedCharacter()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            var reference = JobReferenceGenerator.Generate();

            // Only the random part is checked: "JOB-" is fixed, and the O in it
            // is not a character anyone has to read back one at a time.
            var random = reference[JobReferenceGenerator.Prefix.Length..];

            Assert.All(random, c => Assert.False(
                ExcludedCharacters.Contains(c),
                $"'{c}' is excluded from the alphabet but appeared in {reference}."));
        }
    }

    [Fact]
    public void Generate_DrawsOnlyFromTheAlphabet()
    {
        for (var i = 0; i < SampleSize; i++)
        {
            var reference = JobReferenceGenerator.Generate();
            var random = reference[JobReferenceGenerator.Prefix.Length..];

            Assert.All(random, c => Assert.True(
                JobReferenceGenerator.Alphabet.Contains(c),
                $"'{c}' is not in the alphabet but appeared in {reference}."));
        }
    }

    [Fact]
    public void Alphabet_IsThirtyTwoDistinctCharacters()
    {
        Assert.Equal(32, JobReferenceGenerator.Alphabet.Length);
        // Distinct as well as thirty-two: a repeated character would keep the
        // length right while quietly skewing the draw.
        Assert.Equal(32, JobReferenceGenerator.Alphabet.Distinct().Count());
    }

    [Fact]
    public void Alphabet_ExcludesTheAmbiguousCharacters()
    {
        Assert.All(ExcludedCharacters, c => Assert.False(
            JobReferenceGenerator.Alphabet.Contains(c),
            $"'{c}' should not be in the alphabet."));
    }

    [Fact]
    public void Generate_DoesNotRepeatItself()
    {
        // Not a uniformity test - it is the one property the unique index
        // depends on. Six characters from thirty-two is about a billion
        // references, so a thousand draws colliding would mean the generator is
        // not actually drawing at random.
        var references = Enumerable.Range(0, 1000)
            .Select(_ => JobReferenceGenerator.Generate())
            .ToList();

        Assert.Equal(references.Count, references.Distinct().Count());
    }
}
