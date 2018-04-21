﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IRuettae.Core.Algorithm.GoogleORTools.Detail;
using GLS = Google.OrTools.LinearSolver;

namespace IRuettae.Core.Algorithm.GoogleORTools.TargetFunctionBuilders
{
    class DefaultTargetFunctionBuilder : AbstractTargetFunctionBuilder
    {
        private VariableBuilder variables;
        private GLS.LinearExpr targetFunction = new GLS.LinearExpr();

        public DefaultTargetFunctionBuilder()
        {
        }

        public override void CreateTargetFunction(VariableBuilder variables)
        {
            this.variables = variables;

            var factory = new TargetFunctionFactory(variables);

            targetFunction += factory.CreateTargetFunction(TargetType.ShortestRoute, null);

            variables.Solver.Maximize(targetFunction);
        }
    }
}
