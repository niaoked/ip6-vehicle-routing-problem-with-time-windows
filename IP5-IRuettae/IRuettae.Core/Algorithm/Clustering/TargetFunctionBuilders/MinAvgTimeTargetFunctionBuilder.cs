﻿using IRuettae.Core.Algorithm.Clustering.Detail;
using GLS = Google.OrTools.LinearSolver;

namespace IRuettae.Core.Algorithm.Clustering.TargetFunctionBuilders
{
    internal class MinAvgTimeTargetFunctionBuilder : AbstractTargetFunctionBuilder
    {
        private GLS.LinearExpr targetFunction = new GLS.LinearExpr();

        public override void CreateTargetFunction(SolverData solverData)
        {
            var factory = new TargetFunctionFactory(solverData);

            var factor = solverData.NumberOfSantas / 2;
            targetFunction += (factory.CreateTargetFunction(TargetType.OverallMinTime) - factory.CreateTargetFunction(TargetType.Bonus, null) * 20 * 60) / factor + factory.CreateTargetFunction(TargetType.RealMinTimePerSanta, null);
            solverData.Solver.Minimize(targetFunction);
        }
    }
}