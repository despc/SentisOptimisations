using System;
using SentisOptimisationsPlugin.CrashFix;
using Xunit;

namespace SentisOptimisations.Tests
{
    public class TorchJumpFixTests
    {
        [Fact]
        public void Torchs_jump_is_recognised_with_its_target()
        {
            // mov rax, 0x00007ffb5c2d5020; jmp rax
            var code = new byte[] { 0x48, 0xB8, 0x20, 0x50, 0x2D, 0x5C, 0xFB, 0x7F, 0x00, 0x00, 0xFF, 0xE0 };
            Assert.True(TorchJumpFix.IsTorchJump(code, out var target));
            Assert.Equal(0x00007ffb5c2d5020L, target);
        }

        [Fact]
        public void A_prologue_is_not_a_jump()
        {
            var code = new byte[] { 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x55, 0x53, 0x48, 0x83 };
            Assert.False(TorchJumpFix.IsTorchJump(code, out _));
        }

        [Fact]
        public void The_absolute_jump_is_one_instruction_with_the_target_after_it()
        {
            var code = TorchJumpFix.AbsoluteJump(0x00007ffb5c2d5020L);
            Assert.Equal(14, code.Length);
            Assert.Equal(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }, new ArraySegment<byte>(code, 0, 6));
            Assert.Equal(0x00007ffb5c2d5020L, BitConverter.ToInt64(code, 6));
        }
    }
}
