using Paradise.Rendering.WebGPU.Internal;

namespace Paradise.Rendering.WebGPU.Test;

public class FramePacerTests
{
    [Test]
    public async Task unpaced_target_waits_for_the_previous_frame_before_each_later_submit()
    {
        var waits = 0;
        var pacer = new FramePacer(targetPacesFrames: false, () => waits++);

        pacer.BeforeFrameSubmit();
        await Assert.That(waits).IsEqualTo(0);

        pacer.BeforeFrameSubmit();
        pacer.BeforeFrameSubmit();
        await Assert.That(waits).IsEqualTo(2);
    }

    [Test]
    public async Task paced_target_never_waits()
    {
        var waits = 0;
        var pacer = new FramePacer(targetPacesFrames: true, () => waits++);

        for (var i = 0; i < 4; i++) pacer.BeforeFrameSubmit();

        await Assert.That(waits).IsEqualTo(0);
    }

    [Test]
    public async Task a_failed_wait_propagates_and_the_next_frame_waits_again()
    {
        var waits = 0;
        var pacer = new FramePacer(targetPacesFrames: false, () =>
        {
            waits++;
            if (waits == 1) throw new TimeoutException("stalled");
        });

        pacer.BeforeFrameSubmit();
        await Assert.That(pacer.BeforeFrameSubmit).Throws<TimeoutException>();
        pacer.BeforeFrameSubmit();

        await Assert.That(waits).IsEqualTo(2);
    }
}
