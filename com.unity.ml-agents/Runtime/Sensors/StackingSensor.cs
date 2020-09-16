using System;
using UnityEngine;

namespace Unity.MLAgents.Sensors
{
    /// <summary>
    /// Sensor that wraps around another Sensor to provide temporal stacking.
    /// Conceptually, consecutive observations are stored left-to-right, which is how they're output
    /// For example, 4 stacked sets of observations would be output like
    ///   |  t = now - 3  |  t = now -3  |  t = now - 2  |  t = now  |
    /// Internally, a circular buffer of arrays is used. The m_CurrentIndex represents the most recent observation.
    /// Currently, observations are stacked on the last dimension.
    /// </summary>
    public class StackingSensor : ISensor
    {
        /// <summary>
        /// The wrapped sensor.
        /// </summary>
        ISensor m_WrappedSensor;

        /// <summary>
        /// Number of stacks to save
        /// </summary>
        int m_NumStackedObservations;
        int m_UnstackedObservationSize;

        string m_Name;
        int[] m_Shape;

        /// <summary>
        /// Buffer of previous observations
        /// </summary>
        float[][] m_StackedObservations;

        byte[][] m_StackedCompressedObservations;

        int m_CurrentIndex;
        ObservationWriter m_LocalWriter = new ObservationWriter();

        byte[] m_EmptyCompressedObservation;
        int[] m_CompressedMapping;

        /// <summary>
        /// Initializes the sensor.
        /// </summary>
        /// <param name="wrapped">The wrapped sensor.</param>
        /// <param name="numStackedObservations">Number of stacked observations to keep.</param>
        public StackingSensor(ISensor wrapped, int numStackedObservations)
        {
            // TODO ensure numStackedObservations > 1
            m_WrappedSensor = wrapped;
            m_NumStackedObservations = numStackedObservations;

            m_Name = $"StackingSensor_size{numStackedObservations}_{wrapped.GetName()}";

            var shape = wrapped.GetObservationShape();
            m_Shape = new int[shape.Length];

            m_UnstackedObservationSize = wrapped.ObservationSize();
            for (int d = 0; d < shape.Length; d++)
            {
                m_Shape[d] = shape[d];
            }

            // TODO support arbitrary stacking dimension
            m_Shape[m_Shape.Length - 1] *= numStackedObservations;

            if (m_WrappedSensor.GetCompressionType() == SensorCompressionType.None)
            {
                m_StackedObservations = new float[numStackedObservations][];
                for (var i = 0; i < numStackedObservations; i++)
                {
                    m_StackedObservations[i] = new float[m_UnstackedObservationSize];
                }
            }
            else
            {
                m_StackedCompressedObservations = new byte[numStackedObservations][];
                m_EmptyCompressedObservation = CreateEmptyPNG();
                for (var i = 0; i < numStackedObservations; i++)
                {
                    m_StackedCompressedObservations[i] = m_EmptyCompressedObservation;
                }

                // Construct compressed observation mapping for decompression
                var wrappedMapping = m_WrappedSensor.GetCompressedObservationMapping();
                int wrappedNumChannel = shape[2];
                int wrappedMapLength = wrappedMapping.Length;
                int offset;
                m_CompressedMapping = new int[wrappedMapLength * m_NumStackedObservations];
                for (var i = 0; i < numStackedObservations; i++)
                {
                    offset = wrappedNumChannel * i;
                    for (var j = 0; j < wrappedMapLength; j++)
                    {
                        m_CompressedMapping[j + wrappedMapLength * i] = wrappedMapping[j] > 0 ? wrappedMapping[j] + offset : 0;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public int Write(ObservationWriter writer)
        {
            // First, call the wrapped sensor's write method. Make sure to use our own writer, not the passed one.
            var wrappedShape = m_WrappedSensor.GetObservationShape();
            m_LocalWriter.SetTarget(m_StackedObservations[m_CurrentIndex], wrappedShape, 0);
            m_WrappedSensor.Write(m_LocalWriter);

            // Now write the saved observations (oldest first)
            var numWritten = 0;
            for (var i = 0; i < m_NumStackedObservations; i++)
            {
                var obsIndex = (m_CurrentIndex + 1 + i) % m_NumStackedObservations;
                writer.AddRange(m_StackedObservations[obsIndex], numWritten);
                numWritten += m_UnstackedObservationSize;
            }

            return numWritten;
        }

        /// <summary>
        /// Updates the index of the "current" buffer.
        /// </summary>
        public void Update()
        {
            m_WrappedSensor.Update();
            m_CurrentIndex = (m_CurrentIndex + 1) % m_NumStackedObservations;
        }

        /// <inheritdoc/>
        public void Reset()
        {
            m_WrappedSensor.Reset();
            // Zero out the buffer.
            if (m_WrappedSensor.GetCompressionType() == SensorCompressionType.None)
            {
                for (var i = 0; i < m_NumStackedObservations; i++)
                {
                    Array.Clear(m_StackedObservations[i], 0, m_StackedObservations[i].Length);
                }
            }
            else
            {
                for (var i = 0; i < m_NumStackedObservations; i++)
                {
                    m_StackedCompressedObservations[i] = m_EmptyCompressedObservation;
                }
            }
        }

        /// <inheritdoc/>
        public int[] GetObservationShape()
        {
            return m_Shape;
        }

        /// <inheritdoc/>
        public string GetName()
        {
            return m_Name;
        }

        /// <inheritdoc/>
        public byte[] GetCompressedObservation()
        {
            var compressed = m_WrappedSensor.GetCompressedObservation();
            m_StackedCompressedObservations[m_CurrentIndex] = compressed;

            int bytesLength = 0;
            foreach (byte[] compressedObs in m_StackedCompressedObservations)
            {
                bytesLength += compressedObs.Length;
            }

            byte[] outputBytes = new byte[bytesLength];
            int offset = 0;
            for (var i = 0; i < m_NumStackedObservations; i++)
            {
                var obsIndex = (m_CurrentIndex + 1 + i) % m_NumStackedObservations;
                Buffer.BlockCopy(m_StackedCompressedObservations[obsIndex],
                    0, outputBytes, offset, m_StackedCompressedObservations[obsIndex].Length);
                offset += m_StackedCompressedObservations[obsIndex].Length;
            }

            return outputBytes;
        }

        public int[] GetCompressedObservationMapping()
        {
            return m_CompressedMapping;
        }

        /// <inheritdoc/>
        public SensorCompressionType GetCompressionType()
        {
            return m_WrappedSensor.GetCompressionType();
        }

        /// <summary>
        /// Create Empty PNG for initializing the buffer for stacking.
        /// </summary>
        public byte[] CreateEmptyPNG()
        {
            int height = m_WrappedSensor.GetObservationShape()[0];
            int width = m_WrappedSensor.GetObservationShape()[1];
            var texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
            return texture2D.EncodeToPNG();
        }
    }
}
