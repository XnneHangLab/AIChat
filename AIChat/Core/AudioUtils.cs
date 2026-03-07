using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace AIChat.Core
{
    public static class AudioUtils
    {
        public static AudioClip TrimAudioClip(AudioClip original, int endPosition)
        {
            float[] data = new float[endPosition * original.channels];
            original.GetData(data, 0);

            AudioClip newClip = AudioClip.Create("TrimmedVoice", endPosition, original.channels, original.frequency, false);
            newClip.SetData(data, 0);
            return newClip;
        }

        public static byte[] EncodeToWAV(AudioClip clip)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                float[] samples = new float[clip.samples * clip.channels];
                clip.GetData(samples, 0);

                int hz = clip.frequency;
                int channels = clip.channels;
                int samplesCount = samples.Length;

                byte[] riff = Encoding.UTF8.GetBytes("RIFF");
                stream.Write(riff, 0, 4);

                byte[] chunkSize = BitConverter.GetBytes(samplesCount * 2 + 36);
                stream.Write(chunkSize, 0, 4);

                byte[] wave = Encoding.UTF8.GetBytes("WAVE");
                stream.Write(wave, 0, 4);

                byte[] fmt = Encoding.UTF8.GetBytes("fmt ");
                stream.Write(fmt, 0, 4);

                byte[] subChunk1 = BitConverter.GetBytes(16);
                stream.Write(subChunk1, 0, 4);

                ushort one = 1;
                byte[] audioFormat = BitConverter.GetBytes(one);
                stream.Write(audioFormat, 0, 2);

                byte[] numChannels = BitConverter.GetBytes(channels);
                stream.Write(numChannels, 0, 2);

                byte[] sampleRate = BitConverter.GetBytes(hz);
                stream.Write(sampleRate, 0, 4);

                byte[] byteRate = BitConverter.GetBytes(hz * channels * 2);
                stream.Write(byteRate, 0, 4);

                ushort blockAlign = (ushort)(channels * 2);
                stream.Write(BitConverter.GetBytes(blockAlign), 0, 2);

                ushort bps = 16;
                byte[] bitsPerSample = BitConverter.GetBytes(bps);
                stream.Write(bitsPerSample, 0, 2);

                byte[] datastring = Encoding.UTF8.GetBytes("data");
                stream.Write(datastring, 0, 4);

                byte[] subChunk2 = BitConverter.GetBytes(samplesCount * 2);
                stream.Write(subChunk2, 0, 4);

                short[] intData = new short[samplesCount];
                byte[] bytesData = new byte[samplesCount * 2];
                int rescaleFactor = 32767;

                for (int i = 0; i < samplesCount; i++)
                {
                    intData[i] = (short)(samples[i] * rescaleFactor);
                    byte[] byteArr = BitConverter.GetBytes(intData[i]);
                    byteArr.CopyTo(bytesData, i * 2);
                }

                stream.Write(bytesData, 0, bytesData.Length);
                return stream.ToArray();
            }
        }

        public static bool TryDecodeWavToFloat(byte[] wavBytes, out float[] samples, out int sampleRate, out int channels)
        {
            samples = null;
            sampleRate = 0;
            channels = 0;

            if (wavBytes == null || wavBytes.Length < 44)
                return false;

            try
            {
                using (var stream = new MemoryStream(wavBytes))
                using (var reader = new BinaryReader(stream))
                {
                    string riff = new string(reader.ReadChars(4));
                    if (riff != "RIFF")
                        return false;

                    reader.ReadInt32();
                    string wave = new string(reader.ReadChars(4));
                    if (wave != "WAVE")
                        return false;

                    ushort audioFormat = 1;
                    ushort bitsPerSample = 16;
                    byte[] dataChunk = null;

                    while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                    {
                        string chunkId = new string(reader.ReadChars(4));
                        int chunkSize = reader.ReadInt32();

                        if (chunkSize < 0 || reader.BaseStream.Position + chunkSize > reader.BaseStream.Length)
                            return false;

                        if (chunkId == "fmt ")
                        {
                            audioFormat = reader.ReadUInt16();
                            channels = reader.ReadUInt16();
                            sampleRate = reader.ReadInt32();
                            reader.ReadInt32();
                            reader.ReadUInt16();
                            bitsPerSample = reader.ReadUInt16();

                            int remaining = chunkSize - 16;
                            if (remaining > 0)
                                reader.ReadBytes(remaining);
                        }
                        else if (chunkId == "data")
                        {
                            dataChunk = reader.ReadBytes(chunkSize);
                        }
                        else
                        {
                            reader.ReadBytes(chunkSize);
                        }

                        if ((chunkSize & 1) == 1 && reader.BaseStream.Position < reader.BaseStream.Length)
                            reader.ReadByte();
                    }

                    if (dataChunk == null || sampleRate <= 0 || channels <= 0)
                        return false;

                    if (audioFormat == 3 && bitsPerSample == 32)
                    {
                        int count = dataChunk.Length / 4;
                        samples = new float[count];
                        Buffer.BlockCopy(dataChunk, 0, samples, 0, dataChunk.Length);
                        return true;
                    }

                    if (audioFormat == 1 && bitsPerSample == 16)
                    {
                        int count = dataChunk.Length / 2;
                        samples = new float[count];
                        for (int i = 0; i < count; i++)
                        {
                            short value = BitConverter.ToInt16(dataChunk, i * 2);
                            samples[i] = value / 32768f;
                        }
                        return true;
                    }

                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
