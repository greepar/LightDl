using System.Reflection;
using System.Text;
using Xunit;

namespace LightDl.Tests;

public sealed class FileNameTests
{
    private static string Sanitize(string value)
    {
        var method = typeof(LightDownloader).GetMethod("SanitizeFileName",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [value])!;
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")]
    [InlineData("..", "download")]
    [InlineData(".", "download")]
    [InlineData("   ", "download")]
    [InlineData("a/b\\c.txt", "a_b_c.txt")]
    [InlineData("trailing.  ", "trailing")]
    public void Sanitize_Produces_A_Single_Safe_Segment(string input, string expected)
    {
        Assert.Equal(expected, Sanitize(input));
    }

    [Fact]
    public void Sanitize_Strips_Control_Characters()
    {
        Assert.Equal("a_b.txt", Sanitize("a\u0007b.txt"));
        Assert.Equal("a_b.txt", Sanitize("a\nb.txt"));
        Assert.Equal("a_b.txt", Sanitize("a\u0000b.txt"));
    }

    [Fact]
    public void Sanitize_Truncates_Long_Names_But_Keeps_The_Extension()
    {
        var name = new string('x', 400) + ".tar.gz";
        var result = Sanitize(name);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 255);
        Assert.EndsWith(".gz", result);
    }

    [Fact]
    public void Sanitize_Truncates_Multibyte_Names_Without_Splitting_Characters()
    {
        var name = new string('中', 200) + ".zip";
        var result = Sanitize(name);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 255);
        Assert.DoesNotContain('�', result);
        Assert.EndsWith(".zip", result);
    }

    [Fact]
    public void Sanitize_Truncates_Surrogate_Pairs_Cleanly()
    {
        var name = string.Concat(Enumerable.Repeat("\U0001F600", 100)) + ".png";
        var result = Sanitize(name);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 255);
        Assert.False(char.IsHighSurrogate(result[^5]), "a dangling high surrogate was left behind");
    }

    [Fact]
    public void Sanitize_Never_Escapes_The_Destination_Directory()
    {
        var directory = Directory.CreateTempSubdirectory("lightdl-name-").FullName;
        try
        {
            foreach (var hostile in new[] { "../../evil", "..", "/etc/passwd", "\\..\\evil", "." })
            {
                var combined = Path.GetFullPath(Path.Combine(directory, Sanitize(hostile)));
                Assert.Equal(directory, Path.GetDirectoryName(combined));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
