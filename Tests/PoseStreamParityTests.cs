using Xunit;

namespace HumankindAssetFramework.Tests
{
    // PoseStreamParity.Compare - the acceptance row's rule for the two routes' baked pose streams (editor/PoseStreamParity.cs):
    // the SDK's FrameCount is ceil(clip length x frame rate), and Unity's FBX import reads a clip's length a float ulp above the
    // GLB route's, so the two routes land one frame apart on some clips (measured 2026-10-27: the ArmouredCar 8 against 7 frames,
    // the GatlingGuns 26 against 25, the ReconDrone 213 against 212); anything else is a real difference.
    public class PoseStreamParityTests
    {
        [Fact]
        public void Equal_frames_need_equal_bytes()
        {
            Assert.Null(PoseStreamParity.Compare(7, 16, 1428, 7, 16, 1428));
            Assert.Contains("bone encodings differ", PoseStreamParity.Compare(7, 16, 1428, 7, 16, 1440));
        }

        [Fact]
        public void One_frame_apart_passes_with_one_frames_bytes()
        {
            Assert.Null(PoseStreamParity.Compare(8, 16, 1632, 7, 16, 1428));       // the ArmouredCar: 204 bytes a frame
            Assert.Null(PoseStreamParity.Compare(26, 3, 1248, 25, 3, 1200));       // the GatlingGuns: 48
            Assert.Null(PoseStreamParity.Compare(213, 87, 112992, 212, 87, 111948)); // the ReconDrone: 1044
            Assert.Null(PoseStreamParity.Compare(7, 16, 1428, 8, 16, 1632));       // the GLB route a frame longer
        }

        [Fact]
        public void One_frame_apart_with_the_wrong_bytes_fails()
        {
            Assert.NotNull(PoseStreamParity.Compare(8, 16, 1428, 7, 16, 1632));   // the extra frame has FEWER bytes
            Assert.NotNull(PoseStreamParity.Compare(8, 16, 1700, 7, 16, 1428));   // 272 bytes apart: more than 16 a bone
        }

        [Fact]
        public void Two_frames_apart_other_bones_or_no_frames_fail()
        {
            Assert.Contains("frames", PoseStreamParity.Compare(9, 16, 1836, 7, 16, 1428));
            Assert.Contains("bones", PoseStreamParity.Compare(7, 16, 1428, 7, 17, 1428));
            Assert.NotNull(PoseStreamParity.Compare(0, 16, 0, 7, 16, 1428));
        }

        [Fact]
        public void Held_streams_allow_the_rounding_frame_in_either_direction()
        {
            Assert.Null(PoseStreamParity.Compare(8, 3, 48, 7, 3, 48));
            Assert.Null(PoseStreamParity.Compare(7, 3, 48, 8, 3, 48));
            Assert.Null(PoseStreamParity.Compare(4, 1, 24, 3, 1, 24)); // the fourth rotation-only frame shares the second pair
            Assert.Null(PoseStreamParity.Compare(3, 1, 24, 4, 1, 24));
        }

        [Theory]
        [InlineData(1, 113, 112)] // not a whole encoded word
        [InlineData(1, 100, 96)]  // four bytes cannot encode an animated bone's frame
        [InlineData(2, 120, 100)] // twenty bytes cannot be a sum of 12/16-byte bone frames
        public void Rounding_requires_a_possible_encoded_frame(int bones, long longer, long shorter)
        {
            Assert.NotNull(PoseStreamParity.Compare(8, bones, longer, 7, bones, shorter));
            Assert.NotNull(PoseStreamParity.Compare(7, bones, shorter, 8, bones, longer));
        }
    }
}
