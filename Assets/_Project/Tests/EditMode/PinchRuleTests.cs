using CardboardXR.Hands;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class PinchRuleTests
    {
        const float k_Open = 1.9f;
        const float k_CloseNotTouching = 0.45f;
        const float k_Pinched = 0.23f;

        static PinchRule Steady()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults; i++)
                rule.Update(true, k_Open);
            return rule;
        }

        [Test]
        public void ClosingFingertipsOnASteadyHandPinchesAfterDebounce()
        {
            var rule = Steady();
            for (var i = 1; i < PinchSensor.k_CloseDebounceResults; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
            Assert.IsTrue(rule.IsPinched);
        }

        [Test]
        public void CloseButNotTouchingFingersDoNotPinch()
        {
            var rule = Steady();
            for (var i = 0; i < 10; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_CloseNotTouching));
            Assert.IsFalse(rule.IsPinched);
        }

        [Test]
        public void AHandArrivingAlreadyPinchedNeverTriggersAPinch()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults + 10; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            Assert.IsFalse(rule.IsPinched);
        }

        [Test]
        public void OnePinchClicksOnceUntilTheFingersOpen()
        {
            var rule = Steady();
            for (var i = 1; i < PinchSensor.k_CloseDebounceResults; i++)
                rule.Update(true, k_Pinched);
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));

            // In between the thresholds it stays pinched: no second click from jitter.
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, PinchSensor.k_CloseRatio + 0.1f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(true, k_Open));

            for (var i = 1; i < PinchSensor.k_CloseDebounceResults; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
        }

        [Test]
        public void LosingTheHandOpensAndStartsTheWaitAgain()
        {
            var rule = Steady();
            for (var i = 1; i < PinchSensor.k_CloseDebounceResults; i++)
                rule.Update(true, k_Pinched);
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched));
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(false, 0f));
            Assert.IsFalse(rule.IsPinched);
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched));
        }
    }
}
