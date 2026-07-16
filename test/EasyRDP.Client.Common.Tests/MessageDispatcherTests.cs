using EasyRDP.Client.Common;
using EasyRDP.Core.Protocol;
using Xunit;

namespace EasyRDP.Client.Common.Tests;

public class MessageDispatcherTests
{
    [Fact]
    public void Register_And_Dispatch_ShouldCallHandler()
    {
        var dispatcher = new MessageDispatcher();
        string received = null!;

        dispatcher.Register<KeepAliveAckMessage>(msg => received = "ack");
        dispatcher.Dispatch(new KeepAliveAckMessage());

        Assert.Equal("ack", received);
    }

    [Fact]
    public void Dispatch_UnregisteredType_ShouldNotThrow()
    {
        var dispatcher = new MessageDispatcher();
        // Should not throw
        dispatcher.Dispatch(new KeepAliveMessage());
    }

    [Fact]
    public void Dispatch_Null_ShouldNotThrow()
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.Dispatch(null!);
    }

    [Fact]
    public void Unregister_ShouldPreventDispatch()
    {
        var dispatcher = new MessageDispatcher();
        int count = 0;

        dispatcher.Register<KeepAliveAckMessage>(_ => count++);
        dispatcher.Dispatch(new KeepAliveAckMessage());
        Assert.Equal(1, count);

        dispatcher.Unregister<KeepAliveAckMessage>();
        dispatcher.Dispatch(new KeepAliveAckMessage());
        Assert.Equal(1, count); // not called again
    }

    [Fact]
    public void MultipleHandlers_DifferentTypes_ShouldBothFire()
    {
        var dispatcher = new MessageDispatcher();
        int ackCount = 0, hshakeCount = 0;

        dispatcher.Register<KeepAliveAckMessage>(_ => ackCount++);
        dispatcher.Register<HandshakeResMessage>(_ => hshakeCount++);

        dispatcher.Dispatch(new KeepAliveAckMessage());
        dispatcher.Dispatch(new HandshakeResMessage());
        dispatcher.Dispatch(new KeepAliveAckMessage());

        Assert.Equal(2, ackCount);
        Assert.Equal(1, hshakeCount);
    }

    [Fact]
    public void Register_NullHandler_ShouldThrow()
    {
        var dispatcher = new MessageDispatcher();
        Assert.Throws<ArgumentNullException>(() => dispatcher.Register<KeepAliveAckMessage>(null!));
    }

    [Fact]
    public void Clear_ShouldRemoveAll()
    {
        var dispatcher = new MessageDispatcher();
        int count = 0;
        dispatcher.Register<KeepAliveAckMessage>(_ => count++);
        dispatcher.Clear();
        dispatcher.Dispatch(new KeepAliveAckMessage());
        Assert.Equal(0, count);
    }

    [Fact]
    public void OnLog_ShouldBeCalledForUnregisteredTypes()
    {
        var dispatcher = new MessageDispatcher();
        string lastLog = null!;
        dispatcher.OnLog = msg => lastLog = msg;

        dispatcher.Dispatch(new KeepAliveMessage());
        Assert.NotNull(lastLog);
        Assert.Contains("KeepAliveMessage", lastLog);
    }

    [Fact]
    public void ReRegister_SameType_ShouldOverwrite()
    {
        var dispatcher = new MessageDispatcher();
        int firstCount = 0, secondCount = 0;

        dispatcher.Register<KeepAliveAckMessage>(_ => firstCount++);
        dispatcher.Register<KeepAliveAckMessage>(_ => secondCount++);
        dispatcher.Dispatch(new KeepAliveAckMessage());

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }
}
