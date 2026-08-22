using System.Threading.Tasks;
using NUnit.Framework;
using Picollo.Internal.SyncPipelines;
using Picollo.Internal.SyncPipelines.Framed;
using Shouldly;

namespace Picollo.Tests.Internal.SyncPipelines.Framed;

[TestFixture]
public class FramdPipeTest
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    public async Task should_return_when_writer_completes(int expected)
    {
        await using var fp = new FramedPipe(new SyncPipeOptions());

        for (int i = 0; i < expected; i++)
        {
            using (fp.Writer.WriteFrame()) ;    
        }
        
        await fp.Writer.CompleteAsync();

        var c = 0;

        await foreach (var f in fp.Reader.ConsumeFramesAsync())
        {
            c++;
        }

        c.ShouldBe(expected);
    }
}