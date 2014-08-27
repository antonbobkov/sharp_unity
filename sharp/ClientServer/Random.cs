using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerClient
{
    class Random
    {
        //m_w = <choose-initializer>;    // must not be zero, nor 0x464fffff
        //m_z = <choose-initializer>;    // must not be zero, nor 0x9068ffff

        UInt32 m_w;
        UInt32 m_z;

        public Random():this(System.DateTime.Now.Millisecond)
        {}
        
        public Random(Int32 seed_)
        {
            UInt32 seed = (UInt32)seed_;
            
            m_w = seed & 0xAAAAAAAA;
            m_z = seed & 0x55555555;

            if (m_w == 0 || m_w == 0x464fffff)
                ++m_w;
            if (m_z == 0 || m_z == 0x9068ffff)
                ++m_z;
        }
 
        UInt32 Next()
        {
            m_z = 36969 * (m_z & 65535) + (m_z >> 16);
            m_w = 18000 * (m_w & 65535) + (m_w >> 16);
            return (m_z << 16) + m_w;  // 32-bit result
        }

        public double NextDouble()
        {
            return Convert.ToDouble(Next())/
                    Convert.ToDouble(UInt32.MaxValue);
        }

        public Int32 Next(Int32 maxValue)
        {
            if (maxValue < 0)
                throw new ArgumentOutOfRangeException("maxValue", "cannot be negative");
            if (maxValue == 0)
                return 0;
            return (Int32)(Next()%((UInt32)maxValue));
        }

        public Int32 Next(Int32 minValue, Int32 maxValue)
        {
            return minValue + Next(maxValue - minValue);
        }
    }
}
