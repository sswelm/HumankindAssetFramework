using System;
using Xunit;

/// <summary>Stage 3 part d5 (the oars) of vehicle_rig.py in C#: CPython 3.13's math.hypot, which the oar clustering compares with the
/// merge eps. The cases are Blender 5.1.2's Python (2026-10-17): 24 random pairs where hypot differs from sqrt(a * a + b * b), plus
/// zeros, subnormals, a huge pair, infinity beside NaN and NaN - as IEEE bits.</summary>
public class VehicleRigOarTests
{
    static double H(string h) => BitConverter.Int64BitsToDouble(Convert.ToInt64(h, 16));

    [Theory]
    [InlineData("bfee24c74146f792", "3f769b45471997cf", "3fee24e929a392f7")]
    [InlineData("bfb09528ac6f68e2", "3f4f22d916074437", "3fb0959d97787532")]
    [InlineData("3feb7f79068e526e", "bf9fdedff2e14e60", "3feb8416ac65ae61")]
    [InlineData("40395c7491b8c165", "3fd836df74f2e84c", "40395d2d839d688b")]
    [InlineData("bf462e97e186530c", "c020c2fcda110670", "4020c2fcdafbddc9")]
    [InlineData("bf5571e8948fe05c", "bfec3d1a293dfc28", "3fec3d1c326232dc")]
    [InlineData("bfda4391edbda28c", "4020e15071ff1a9b", "4020e66b54b6a8be")]
    [InlineData("bf439b9ac0339a3e", "c0883708ceedffa0", "40883708ceee0790")]
    [InlineData("c084786e82b42d72", "3fa34923ec28409d", "4084786e834588a9")]
    [InlineData("bfaedba0287dc340", "3febfe0fc6d4b1d2", "3fec0f0cc33efc2c")]
    [InlineData("bfd6315c010ff880", "bfedb7c964ad13b4", "3fefb8d47946056e")]
    [InlineData("3f8473d8ec36e840", "c0203506b2860a38", "402035078100e072")]
    [InlineData("3fd03065d64cbb08", "bfa52d5a33bbd747", "3fd0676fca8f8a80")]
    [InlineData("3f9d246fda952a0d", "bf45513f5084a533", "3f9d2662c7afcdfc")]
    [InlineData("3ffeb3362f4f529a", "bf3e1b03de0cdfd7", "3ffeb3363e12349f")]
    [InlineData("c01e3fc643663fd6", "3f82e0299bdedca5", "401e3fc7bc4fc764")]
    [InlineData("408b340cd4148e56", "bfeda5ad0e08e352", "408b340dd69095a9")]
    [InlineData("40072e258d450699", "3f48f2695e278f75", "40072e259ab19932")]
    [InlineData("c0136c99c8d03d0b", "3f7cd3e830542c48", "40136c9b1f152fb0")]
    [InlineData("3f15b57326de1d50", "bf81ad7395d95256", "3f81ada8e71c8465")]
    [InlineData("3f7c0592ce0d8cda", "406ca38d65ede637", "406ca38d6624bc36")]
    [InlineData("402b759369913a56", "3fbdebfbb9c05b20", "402b75d49f1d8b23")]
    [InlineData("405586e97f160303", "bfd35eb973436a00", "405586f2360303d3")]
    [InlineData("c0385f49fe96e0a4", "40508e162dfd4cba", "4051a404c1e11ad6")]
    [InlineData("0000000000000000", "0000000000000000", "0000000000000000")]
    [InlineData("8000000000000000", "4008000000000000", "4008000000000000")]
    [InlineData("4008000000000000", "4010000000000000", "4014000000000000")]
    [InlineData("000012688b70e62b", "000024d116e1cc56", "0000292991fdae04")]
    [InlineData("0000000000000001", "0000000000000000", "0000000000000001")]
    [InlineData("7e37e43c8800759c", "7e37e43c8800759c", "7e40e4d50f99b211")]
    [InlineData("7ff0000000000000", "7ff8000000000000", "7ff0000000000000")]
    [InlineData("7ff8000000000000", "3ff0000000000000", "7ff8000000000000")]
    public void Hypot_equals_CPython_bit_for_bit(string a, string b, string h)
    {
        double r = BlenderVehicleRig.PyHypot(H(a), H(b));
        Assert.Equal(h, BitConverter.DoubleToInt64Bits(r).ToString("x16"));
    }

    // CPython 3.12+'s sum() of floats from the int start 0 is compensated (Neumaier): 20 random lists where it differs from adding in
    // order, then 1 + 1e-16 + 1e-16, negative zeros, infinities, an overflow and NaN - Blender 5.1.2's Python, as IEEE bits
    [Theory]
    [InlineData("3f0d11cda2a1fe81,3ef1f1914c3a0681,bd8859c6c8d40695,3d99e4d62c7ee893,be0cbc159e18df24", "3f13053cd40ca49c")]
    [InlineData("c08cccaf133bf63c,bfb5a0367b1b53bd,3e19176dfcb31cad,be612aaedc0634c6,3fc8289fcfe4c288", "c08ccbd98af6e947")]
    [InlineData("402237fe18bca89a,bcb3937c0a675fde,bf49354c119f1b33", "40223799438c621d")]
    [InlineData("bdc58e4997bf3a35,bccdd71464f175b2,3dcb73b2333a6659,bcf34171b7cca540,3d22641ac3722417", "3da7a526fe27955a")]
    [InlineData("3ee050f94e83a321,3e38d627c635746e,beabbb9558c07434,bc60fd3c5aa71957,3d294f6161980c26", "3edd30b57f0a8f4a")]
    [InlineData("40559cc29ab33015,bd5b716bc2b2a464,bf7134d72ea2dbcf", "40559c7dc756756e")]
    [InlineData("3f4152689620b0ac,c01c0afea528820a,3c4a6b58e7fbf08b,3f7a9cbf7931805c,3f64dcb2463b1e52", "c01c01314bbcbd41")]
    [InlineData("40276930a689fcec,bfb8a2a6c8caca64,3f37ae1574a598fe,bf08c4a2ddf5b2be,3f78632b9013f403", "40273b20e96c9ba3")]
    [InlineData("bf194bf210f199a0,bf81591824c31a8d,3f188ad7b4992842,3eb09324d5ec16c0,bd646b1ba2ba1375", "bf815a15c05a36d6")]
    [InlineData("4053369ca62e6b08,bf9478b21bd0fc3a,3c66b3d57c22ce94,bee2e576d8c80ea1,bcd1b453753d023a", "40533554f541c047")]
    [InlineData("be927aa70a0f8505,be04bf706dc264f1,3f7396e73bdab592,bf4c5947730e0a9c,3e1024ebc89618f4", "3f700b7479f19a47")]
    [InlineData("3d835e631b2cfab3,c03de4ff8fbe23c0,c021be4a044195e6,bea7bb17c6970df7,bc3070f752821d47", "c04362124ede3c15")]
    [InlineData("bc9195de9c6049b5,3e411e1f8e201cbd,be278a745ccf5af7", "3e367704e9731457")]
    [InlineData("3ed84f62118ab71d,3cfc5accb1a9500a,3d381990165ed49a,be10e179942923b2,3cfd123abbb89f1d", "3ed84e5400dd8cae")]
    [InlineData("3e3b0949a02663d5,be538b0efaaddc9e,be05a2fc697b4e6b", "be4aeba8ebe03c38")]
    [InlineData("be51f176b4ebda07,3e93643bc82125f6,3d600bda97ce63a1,3ee289cdbe00d7a1,3ee0d879bc7fe48f", "3ef1fa3856b99505")]
    [InlineData("3cf8983535b75089,bfb8d729b2de8095,bea7fddbac0c4bfb,c02070524822bbc2,3f817fce3f56b64f", "c0209da0bff67ebf")]
    [InlineData("40035e55a016f5fa,3f46b2cfd8a584c5,bd664f2f5eb1c594", "40035fc0cd147abf")]
    [InlineData("4024054ee07352b8,404bcf018ad3e29f,bfc4762b96232168,bea0d3b33e4f06ec,3f00a7e6c6f775c3", "40505df00ed209e6")]
    [InlineData("bc27b3fa5f09ffd8,3f2f2ab2f135b2a0,bedd2189877c5a51,3f0ba3063a1438a7,3f460e4a67defbc9", "3f4f58e474beb341")]
    [InlineData("3ff0000000000000,3c9cd2b297d889bc,3c9cd2b297d889bc", "3ff0000000000001")]
    [InlineData("8000000000000000", "0000000000000000")]
    [InlineData("8000000000000000,8000000000000000", "0000000000000000")]
    [InlineData("7ff0000000000000,3ff0000000000000,fff0000000000000", "fff8000000000000")]
    [InlineData("7fe1ccf385ebc8a0,7fe1ccf385ebc8a0,ffe1ccf385ebc8a0", "7ff0000000000000")]
    [InlineData("7ff8000000000000,3ff0000000000000", "7ff8000000000000")]
    [InlineData("4000000000000000", "4000000000000000")]
    public void Sum_equals_CPython_bit_for_bit(string items, string sum)
    {
        double r = BlenderVehicleRig.PySum(System.Linq.Enumerable.Select(items.Split(','), H));
        Assert.Equal(sum, BitConverter.DoubleToInt64Bits(r).ToString("x16"));
    }
}
