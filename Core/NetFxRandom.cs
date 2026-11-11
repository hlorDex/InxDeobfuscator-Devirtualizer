using System;

namespace Deobfuscator.Core
{
    public sealed class NetFxRandom
    {
        private const int MBIG = int.MaxValue;
        private const int MSEED = 161803398;
        private readonly int[] _seedArray = new int[56];
        private int _inext;
        private int _inextp;

        public NetFxRandom(int seed)
        {
            int subtraction = seed == int.MinValue ? int.MaxValue : Math.Abs(seed);
            int mj = MSEED - subtraction;
            _seedArray[55] = mj;
            int mk = 1;
            for (int i = 1; i < 55; i++)
            {
                int ii = (21 * i) % 55;
                _seedArray[ii] = mk;
                mk = mj - mk;
                if (mk < 0) mk += MBIG;
                mj = _seedArray[ii];
            }
            for (int k = 1; k < 5; k++)
            {
                for (int i = 1; i < 56; i++)
                {
                    _seedArray[i] -= _seedArray[1 + (i + 30) % 55];
                    if (_seedArray[i] < 0) _seedArray[i] += MBIG;
                }
            }
            _inext = 0;
            _inextp = 21;
        }

        private int InternalSample()
        {
            int locInext = _inext;
            int locInextp = _inextp;
            if (++locInext >= 56) locInext = 1;
            if (++locInextp >= 56) locInextp = 1;
            int retVal = _seedArray[locInext] - _seedArray[locInextp];
            if (retVal == MBIG) retVal--;
            if (retVal < 0) retVal += MBIG;
            _seedArray[locInext] = retVal;
            _inext = locInext;
            _inextp = locInextp;
            return retVal;
        }

        private double Sample() => InternalSample() * (1.0 / MBIG);

        public int Next(int minValue, int maxValue)
        {
            long range = (long)maxValue - minValue;
            if (range <= (long)int.MaxValue)
                return (int)(Sample() * range) + minValue;
            return (int)((long)(GetSampleForLargeRange() * range) + minValue);
        }

        private double GetSampleForLargeRange()
        {
            int result = InternalSample();
            if (InternalSample() % 2 == 0) result = -result;
            return (result + 2147483646.0) / 4294967293.0;
        }
    }
}
