using System.Text;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class JsonStructuralFingerprinterTests
{
    [Fact]
    public void OrdinaryValueChangesKeepSameFingerprint()
    {
        var first = Fingerprint("""{"teamId":"WARDENS","iconType":5,"x":0.1}""");
        var second = Fingerprint("""{"teamId":"COLONIALS","iconType":97,"x":0.9}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ObjectAndArrayOrderDoNotChangeFingerprint()
    {
        var first = Fingerprint(
            """{"items":[{"a":1,"b":"x"},{"a":2,"b":"y"}],"version":1}""");
        var second = Fingerprint(
            """{"version":99,"items":[{"b":"z","a":8},{"b":"q","a":7}]}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public void AdditiveFieldChangesFingerprint()
    {
        var before = Fingerprint("""{"teamId":"COLONIALS","iconType":97}""");
        var after = Fingerprint(
            """{"teamId":"COLONIALS","iconType":97,"viewDirection":0}""");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ScalarTypeChangeChangesFingerprint()
    {
        var number = Fingerprint("""{"version":1}""");
        var text = Fingerprint("""{"version":"1"}""");

        Assert.NotEqual(number, text);
    }

    private static string Fingerprint(string json) =>
        JsonStructuralFingerprinter.Compute(Encoding.UTF8.GetBytes(json));
}
