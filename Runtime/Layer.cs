using MachineLearningMath;
using System;
using UnityEngine;
using MachineLearning.Parallelization;
using MachineLearning.Activations;
using MachineLearning.Cost;

namespace MachineLearning
{
    [Serializable]
    public class Layer
    {
        [SerializeField]
        private int _numNodeIn;

        [SerializeField]
        private int _numNodeOut;

        public int ActivationIndex;

        [SerializeField]
        public IActivation activation;

        public int NumNodesIn { get { return _numNodeIn; } set { _numNodeIn = value; } }

        public int NumNodesOut { get { return _numNodeOut; } set { _numNodeOut = value; } }

        public Matrix weights;
        public Matrix biases;

        //Cost Gradient With respect to weight and biases
        private Matrix _costGradientWeight;
        private Matrix _costGradientBias;

        public Matrix CostGradientWeight { get { return _costGradientWeight; } set { _costGradientWeight = value; } }
        public Matrix CostGradientBias { get { return _costGradientBias; } set { _costGradientBias = value; } }

        //Momentum
        private Matrix _weightVelocities;
        private Matrix _biasVelocities;

        public GPUParallelization Parallelization { get; set; }

        public int ParallelBatchSize { get; set; } = 32;

        public Layer(int numNodesIn, int numNodesOut)
        {
            this.NumNodesIn = numNodesIn;
            this.NumNodesOut = numNodesOut;
            activation = new Sigmoid();

            weights = new Matrix(numNodesOut, numNodesIn);
            _costGradientWeight = new Matrix(numNodesOut, numNodesIn);
            _weightVelocities = new Matrix(numNodesOut, numNodesIn);

            biases = new Matrix(numNodesOut, 1);
            _costGradientBias = new Matrix(numNodesOut, 1);
            _biasVelocities = new Matrix(numNodesOut, 1);

            InitializeRandomWeights(new System.Random());

            Parallelization = new GPUParallelization(this);
        }

        public Matrix CalculateOutputs(Matrix inputs)
        {
            if (GPUParallelization.LayerOutputGPU != null)
            {
                if (SystemInfo.deviceType == DeviceType.Desktop)
                    return Parallelization.LayerOutputCalculationTrainingGPU(inputs).activation;
                else
                    return Parallelization.LayerOutputCalculationTrainingGPUFloat(inputs).activation;
            }
              
            else
                return activation.Activate((weights * inputs) + biases);
        }

        public Matrix CalculateOutputs(Matrix inputs, LayerLearnData learnData)
        {
            learnData.inputs = inputs;

            if (GPUParallelization.LayerOutputGPU != null)
            {
                (Matrix weightedInputs, Matrix activation) = Parallelization.LayerOutputCalculationTrainingGPU(inputs);

                //Calculate the outputs
                learnData.weightedInputs = weightedInputs;

                //Apply Activation Function
                learnData.activations = activation;
            }
            else
            {
                //Calculate the outputs
                learnData.weightedInputs = (weights * inputs) + biases;

                //Apply Activation Function
                learnData.activations = activation.Activate(learnData.weightedInputs);
            }

            return learnData.activations;
        }

        public Tensor ParallelCalculateOutputs(Tensor inputs, ParallelLayerLearnData learnData) //LayerLearnData[] learnData
        {
            (Tensor weightedInputs, Tensor activation) = Parallelization.ParallelLayerOutputCalculationTrainingGPU(inputs);

            //Set the Inputs
            learnData.inputs = inputs;

            //Set the Weighted Inputs
            learnData.weightedInputs = weightedInputs;

            //Set the Activations
            learnData.activations = activation;

            return activation;
        }

        public void ApplyGradients(double learnRate, double regularization, double momentum)
        {
            double weightDecay = (1 - regularization * learnRate);

            //Calculate Velocities and Apply them to the respective matrices
            _weightVelocities = _weightVelocities * momentum - _costGradientWeight * learnRate;
            weights = weights * weightDecay + _weightVelocities;

            _biasVelocities = _biasVelocities * momentum - _costGradientBias * learnRate;
            biases += _biasVelocities;

            //Reset Gradients
            _costGradientWeight = new Matrix(_costGradientWeight.Height, _costGradientWeight.Width);
            _costGradientBias = new Matrix(_costGradientBias.Height, _costGradientBias.Width);
        }

        public void CalculateOutputLayerNodeValues(LayerLearnData layerLearnData, Matrix expectedOutputs, ICost cost)
        {
            Matrix costDerivative = cost.CostDerivative(layerLearnData.activations, expectedOutputs);
            Matrix activationDerivative = activation.Derivative(layerLearnData.weightedInputs);

            for (int i = 0; i < layerLearnData.nodeValues.Values.Length; i++)
                layerLearnData.nodeValues[i] = costDerivative[i] * activationDerivative[i];
        }

        public void ParallelCalculateOutputLayerNodeValues(ParallelLayerLearnData layerLearnData, Tensor expectedOutput, ICost cost, Matrix expectedOutputDim)
        {
            Parallelization.ParallelCalculateOutputLayerNodeValues(layerLearnData, expectedOutput, cost, expectedOutputDim);
        }

        public void CalculateHiddenLayerNodeValues(LayerLearnData layerLearnData, Layer oldLayer, Matrix oldNodeValues)
        {
            Matrix newNodeValues = oldLayer.weights.Transpose() * oldNodeValues;

            Matrix derivative = activation.Derivative(layerLearnData.weightedInputs);

            for (int newNodeIndex = 0; newNodeIndex < newNodeValues.Values.Length; newNodeIndex++)
                newNodeValues[newNodeIndex] *= derivative[newNodeIndex];

            layerLearnData.nodeValues = newNodeValues;
        }

        public void ParallelCalculateHiddenLayerNodeValues(ParallelLayerLearnData layerLearnData, Layer oldLayer, Tensor oldNodeValues)
        {
            Parallelization.ParallelHiddenLayerNodeCalc(layerLearnData, oldLayer, oldNodeValues);
        }

        public void UpdateGradients(LayerLearnData layerLearnData)
        {
            //Lock for Parallel Processing
            lock (_costGradientWeight)
            {
                _costGradientWeight += layerLearnData.nodeValues * layerLearnData.inputs.Transpose();
            }

            lock (_costGradientBias)
            {
                _costGradientBias += layerLearnData.nodeValues;
            }
        }

        public void ParallelUpdateGradients(ParallelLayerLearnData layerLearnData)
        {
            _costGradientBias += Parallelization.ParallelUpdateGradientsBiasGPU(layerLearnData);

            _costGradientWeight += Parallelization.ParallelUpdateGradientsWeightsGPU(layerLearnData);
        }

        public void SetActivationFunction(IActivation activation)
        {
            ActivationIndex = activation.GetActivationFunctionIndex();
            this.activation = activation;
        }

        public void SetActivationFunction(int index)
        {
            ActivationIndex = index;
            this.activation = Activation.GetActivationFromIndex(index);
        }

        public void SetSavedActivationFunction()
        {
            this.activation = Activation.GetActivationFromIndex(ActivationIndex);
        }

        public void InitializeParallelization()
        {
            Parallelization = new GPUParallelization(this);
        }
        public void InitializeRandomWeights(System.Random rng)
        {
            for (int weightIndex = 0; weightIndex < weights.Values.Length; weightIndex++)
            {
                weights[weightIndex] = RandomInNormalDistribution(rng, 0, 1) / Mathf.Sqrt(NumNodesIn);
            }

            double RandomInNormalDistribution(System.Random rng, double mean, double standardDeviation)
            {
                double x1 = 1 - rng.NextDouble();
                double x2 = 1 - rng.NextDouble();

                double y1 = Mathf.Sqrt(-2.0f * Mathf.Log((float)x1)) * Mathf.Cos(2.0f * Mathf.PI * (float)x2);
                return y1 * standardDeviation + mean;
            }
        }
    }
}