// Copyright 2026 CyberCraft Lab, OTH Regensburg, Prof. Christophe Barlieb
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using System.Linq;

namespace CCL_Clay3DP.Analysis
{
    public enum AnalysisChannel
    {
        Clay,
        Robot,
        Combined,
    }

    public class FrameScore
    {
        public double OverhangScore { get; set; } = 1.0;
        public double BondScore { get; set; } = 1.0;
        public double CurvatureScore { get; set; } = 1.0;
        public double TaperScore { get; set; } = 1.0;
        public double WristVelocityScore { get; set; } = 1.0;

        /// <summary>Worst clay-related score at this frame.</summary>
        public double ClayScore => new[] { OverhangScore, BondScore, CurvatureScore, TaperScore }.Min();

        /// <summary>Worst robot-related score at this frame.</summary>
        public double RobotScore => WristVelocityScore;

        /// <summary>Worst overall score at this frame.</summary>
        public double CombinedScore => System.Math.Min(ClayScore, RobotScore);

        public double GetScore(AnalysisChannel channel)
        {
            switch (channel)
            {
                case AnalysisChannel.Clay: return ClayScore;
                case AnalysisChannel.Robot: return RobotScore;
                default: return CombinedScore;
            }
        }
    }

    public class PrintabilityResult
    {
        public List<FrameScore> Scores { get; set; } = new List<FrameScore>();
        public List<string> Issues { get; set; } = new List<string>();

        public int SafeCount(AnalysisChannel ch) =>
            Scores.Count(s => s.GetScore(ch) >= 0.7);
        public int MarginalCount(AnalysisChannel ch) =>
            Scores.Count(s => s.GetScore(ch) >= 0.3 && s.GetScore(ch) < 0.7);
        public int FailCount(AnalysisChannel ch) =>
            Scores.Count(s => s.GetScore(ch) < 0.3);
        public bool OverallPass(AnalysisChannel ch) =>
            Scores.All(s => s.GetScore(ch) >= 0.3);
    }
}
