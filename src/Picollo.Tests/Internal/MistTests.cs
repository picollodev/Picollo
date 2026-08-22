using System.Threading;
using NUnit.Framework;

namespace Picollo.Tests.Internal;

[TestFixture]
public class MiscTests
{
    [Test]
    public void linked_cts()
    {
        var cts1 = new CancellationTokenSource();
        var linked1 = CancellationTokenSource.CreateLinkedTokenSource(cts1.Token);
        linked1.Cancel();
        cts1.Cancel();


        var cts2 = new CancellationTokenSource();
        var linked2 = CancellationTokenSource.CreateLinkedTokenSource(cts1.Token);
        cts2.Cancel();
        linked2.Cancel();
    }
}