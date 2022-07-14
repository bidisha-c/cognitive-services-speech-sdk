//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//

using System;
using System.Linq;

namespace CallCenter
{
    public class UserConfig
    {
        /// This should not change unless the Speech REST API changes.
        private const string partialSpeechEndpoint = ".api.cognitive.microsoft.com";
        /// True to treat input audio as stereo; otherwise, treat it as mono.
        readonly public bool useStereoAudio = false;
        /// Language for sentiment analysis and conversation analysis.
        readonly public string language;
        /// Locale for batch transcription.
        readonly public string locale;
        /// Input audio file URL.
        readonly public string inputAudioURL;
        /// The subscription key for your Speech service subscription.
        readonly public string speechSubscriptionKey;
        /// The endpoint for your Speech service subscription.
        readonly public string speechEndpoint;
        /// The subscription key for your Cognitive Language subscription.
        readonly public string languageSubscriptionKey;
        /// The endpoint for your Cognitive Language subscription.
        readonly public string languageEndpoint;

        public static string? GetCmdOption(string[] args, string option)
        {
            int index = Array.FindIndex(args, x => x.Equals(option, StringComparison.OrdinalIgnoreCase));
            if (index > -1 && index < args.Length - 1)
            {
                // We found the option (for example, "--output"), so advance from that to the value (for example, "filename").
                return args[index + 1];
            }
            else
            {
                return null;
            }
        }

        public static bool CmdOptionExists(string[] args, string option)
        {
            return args.Contains(option);
        }

        public UserConfig(string[] args, string usage)
        {
            string? speechSubscriptionKey = GetCmdOption(args, "--speechKey");
            if (speechSubscriptionKey is null)
            {
                throw new ArgumentException($"Missing Speech subscription key.{Environment.NewLine}Usage: {usage}");
            }
            string? speechRegion = GetCmdOption(args, "--speechRegion");
            if (speechRegion is null)
            {
                throw new ArgumentException($"Missing Speech region.{Environment.NewLine}Usage: {usage}");
            }
            
            string? languageSubscriptionKey = GetCmdOption(args, "--languageKey");
            if (languageSubscriptionKey is null)
            {
                throw new ArgumentException($"Missing Language subscription key.{Environment.NewLine}Usage: {usage}");
            }
            string? languageEndpoint = GetCmdOption(args, "--languageEndpoint");
            if (languageEndpoint is null)
            {
                throw new ArgumentException($"Missing Language endpoint.{Environment.NewLine}Usage: {usage}");
            }

            string? inputAudioURL = GetCmdOption(args, "--input");
            if (inputAudioURL is null)
            {
                throw new ArgumentException($"Missing input audio URL.{Environment.NewLine}Usage: {usage}");
            }
            
            string? language = GetCmdOption(args, "--language");
            if (language is null)
            {
                language = "en";
            }
            string? locale = GetCmdOption(args, "--locale");
            if (locale is null)
            {
                locale = "en-US";
            }
  
            this.useStereoAudio = CmdOptionExists(args, "--stereo");
            this.language = language;
            this.locale = locale;
            this.inputAudioURL = inputAudioURL;
            this.speechSubscriptionKey = speechSubscriptionKey;
            this.speechEndpoint = $"{speechRegion}{partialSpeechEndpoint}";
            this.languageSubscriptionKey = languageSubscriptionKey;
            this.languageEndpoint = languageEndpoint;
        }
    }
}
