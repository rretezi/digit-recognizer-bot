﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DigitRecognizerService;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.BotBuilderSamples
{
    /// <summary>
    /// Main entry point and orchestration for bot.
    /// </summary>
    public class BasicBot : IBot
    {
        // Supported LUIS Intents
        public const string GreetingIntent = "Greeting";
        public const string CancelIntent = "Cancel";
        public const string HelpIntent = "Help";
        public const string NoneIntent = "None";

        /// <summary>
        /// Key in the bot config (.bot file) for the LUIS instance.
        /// In the .bot file, multiple instances of LUIS can be configured.
        /// </summary>
        public static readonly string LuisConfiguration = "BasicBotLuisApplication";

        private readonly IStatePropertyAccessor<GreetingState> _greetingStateAccessor;
        private readonly IStatePropertyAccessor<DialogState> _dialogStateAccessor;
        private readonly UserState _userState;
        private readonly ConversationState _conversationState;
        private readonly BotServices _services;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="BasicBot"/> class.
        /// </summary>
        /// <param name="services">Bot services.</param>
        /// <param name="userState">User state.</param>
        /// <param name="conversationState">Conversation state.</param>
        /// <param name="loggerFactory">Logger.</param>
        /// <param name="configuration">Application configuration.</param>
        public BasicBot(BotServices services, UserState userState, ConversationState conversationState, ILoggerFactory loggerFactory, IConfiguration configuration)
        {
            _configuration = configuration;
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _userState = userState ?? throw new ArgumentNullException(nameof(userState));
            _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));

            _greetingStateAccessor = _userState.CreateProperty<GreetingState>(nameof(GreetingState));
            _dialogStateAccessor = _conversationState.CreateProperty<DialogState>(nameof(DialogState));

            // Verify LUIS configuration.
            if (!_services.LuisServices.ContainsKey(LuisConfiguration))
            {
                throw new InvalidOperationException($"The bot configuration does not contain a service type of `luis` with the id `{LuisConfiguration}`.");
            }

            Dialogs = new DialogSet(_dialogStateAccessor);
            Dialogs.Add(new GreetingDialog(_greetingStateAccessor, loggerFactory));
        }

        private DialogSet Dialogs { get; set; }

        /// <summary>
        /// Run every turn of the conversation. Handles orchestration of messages.
        /// </summary>
        /// <param name="turnContext">Bot Turn Context.</param>
        /// <param name="cancellationToken">Task CancellationToken.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            var activity = turnContext.Activity;

            // Create a dialog context
            var dc = await Dialogs.CreateContextAsync(turnContext);

            if (activity.Type == ActivityTypes.Message)
            {
                if (activity.Attachments != null && activity.Attachments.Any())
                {
                    // We know the user is sending an attachment as there is at least one item
                    // in the Attachments list.
                    await HandleIncomingAttachmentAsync(dc, activity);
                    return;
                }

                if (activity.Text?.Trim()?.ToLowerInvariant() == "hi")
                {
                    await turnContext.SendActivitiesAsync(
                        new IActivity[]
                        {
                            new Activity(type: ActivityTypes.Message, text: "Hi! 🙋‍"),
                            new Activity(type: ActivityTypes.Message, text: "Send me a picture of a handwritten digit and I'll tell you what the number is!"),
                            new Activity(type: ActivityTypes.Message, text: "Yeah, I'm that smart! 😎"),
                        },
                        cancellationToken);
                }

                // Perform a call to LUIS to retrieve results for the current activity message.
                var luisResults = await _services.LuisServices[LuisConfiguration].RecognizeAsync(dc.Context, cancellationToken);

                // If any entities were updated, treat as interruption.
                // For example, "no my name is tony" will manifest as an update of the name to be "tony".
                var topScoringIntent = luisResults?.GetTopScoringIntent();

                var topIntent = topScoringIntent.Value.intent;

                // update greeting state with any entities captured
                await UpdateGreetingState(luisResults, dc.Context);

                // Handle conversation interrupts first.
                var interrupted = await IsTurnInterruptedAsync(dc, topIntent);
                if (interrupted)
                {
                    // Bypass the dialog.
                    // Save state before the next turn.
                    await _conversationState.SaveChangesAsync(turnContext);
                    await _userState.SaveChangesAsync(turnContext);
                    return;
                }

                // Continue the current dialog
                var dialogResult = await dc.ContinueDialogAsync();

                // if no one has responded,
                if (!dc.Context.Responded)
                {
                    // examine results from active dialog
                    switch (dialogResult.Status)
                    {
                        case DialogTurnStatus.Empty:
                            switch (topIntent)
                            {
                                case GreetingIntent:
                                    await dc.BeginDialogAsync(nameof(GreetingDialog));
                                    break;

                                case NoneIntent:
                                default:
                                    // Help or no intent identified, either way, let's provide some help.
                                    // to the user
                                    await dc.Context.SendActivityAsync("I didn't understand what you just said to me.");
                                    break;
                            }

                            break;

                        case DialogTurnStatus.Waiting:
                            // The active dialog is waiting for a response from the user, so do nothing.
                            break;

                        case DialogTurnStatus.Complete:
                            await dc.EndDialogAsync();
                            break;

                        default:
                            await dc.CancelAllDialogsAsync();
                            break;
                    }
                }
            }

            await _conversationState.SaveChangesAsync(turnContext);
            await _userState.SaveChangesAsync(turnContext);
        }

        /// <summary>
        /// Handle attachments uploaded by users. The bot receives an <see cref="Attachment"/> in an <see cref="Activity"/>.
        /// The activity has a <see cref="IList{T}"/> of attachments.
        /// </summary>
        /// <remarks>
        /// Not all channels allow users to upload files. Some channels have restrictions
        /// on file type, size, and other attributes. Consult the documentation for the channel for
        /// more information. For example Skype's limits are here
        /// <see ref="https://support.skype.com/en/faq/FA34644/skype-file-sharing-file-types-size-and-time-limits"/>.
        /// </remarks>
        private async Task HandleIncomingAttachmentAsync(DialogContext dc, IMessageActivity activity)
        {
            foreach (var file in activity.Attachments)
            {
                if (file.ContentType != "image/png" && file.ContentType != "image/jpeg")
                {
                    await dc.Context.SendActivityAsync("Sorry, I cannot process images other than png/jpeg.");
                }

                // Download the actual attachment
                using (var client = new HttpClient())
                {
                    var stream = await client.GetStreamAsync(file.ContentUrl);
                    var memoryStream = new MemoryStream();
                    await stream.CopyToAsync(memoryStream);

                    var byteArray = memoryStream.ToArray();

                    //var recognizer = new CustomVisionDigitRecognizer(
                    //    _configuration["CustomVisionBaseUrl"],
                    //    _configuration["CustomVisionProjectId"],
                    //    _configuration["CustomVisionPublishedName"],
                    //    _configuration["CustomVisionApiKey"]);

                    var recognizer = new MLStudioDigitRecognizer(
                        _configuration["MLStudioApiUrl"],
                        _configuration["MLStudioApiKey"]);

                    var prediction = await recognizer.PredictAsync(byteArray);

                    await SendPredictionAnswer(dc, prediction.Tag, prediction.Probability);
                }
            }
        }

        private static async Task SendPredictionAnswer(DialogContext dc, int digit, double probability)
        {
            var digitWithArticle = string.Empty;
            switch (digit)
            {
                case 0:
                    digitWithArticle = "a zero";
                    break;
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                case 9:
                    digitWithArticle = $"a {digit}";
                    break;
                case 8:
                    digitWithArticle = $"an {digit}";
                    break;
                default:
                    break;
            }

            if (probability < 0.5)
            {
                await dc.Context.SendActivityAsync($"I'm not 100% sure, but I think this is {digitWithArticle}.");
                return;
            }

            var random = new Random();
            switch (random.Next(0, 4))
            {
                case 0:
                    await dc.Context.SendActivityAsync($"I know! I know! It's {digitWithArticle}!");
                    break;
                case 1:
                    await dc.Context.SendActivityAsync($"OK, this should be {digitWithArticle}!");
                    break;
                case 2:
                    await dc.Context.SendActivityAsync($"This is {digitWithArticle}!");
                    break;
                case 3:
                    await dc.Context.SendActivityAsync($"Easy-peasy! This is {digitWithArticle}.");
                    break;
            }
        }

        // Determine if an interruption has occurred before we dispatch to any active dialog.
        private async Task<bool> IsTurnInterruptedAsync(DialogContext dc, string topIntent)
        {
            // See if there are any conversation interrupts we need to handle.
            if (topIntent.Equals(CancelIntent))
            {
                if (dc.ActiveDialog != null)
                {
                    await dc.CancelAllDialogsAsync();
                    await dc.Context.SendActivityAsync("Ok. I've canceled our last activity.");
                }
                else
                {
                    await dc.Context.SendActivityAsync("I don't have anything to cancel.");
                }

                return true;        // Handled the interrupt.
            }

            if (topIntent.Equals(HelpIntent))
            {
                await dc.Context.SendActivityAsync("Let me try to provide some help.");
                await dc.Context.SendActivityAsync("I understand greetings, being asked for help, or being asked to cancel what I am doing.");
                if (dc.ActiveDialog != null)
                {
                    await dc.RepromptDialogAsync();
                }

                return true;        // Handled the interrupt.
            }

            return false;           // Did not handle the interrupt.
        }

        // Create an attachment message response.
        private Activity CreateResponse(Activity activity, Attachment attachment)
        {
            var response = activity.CreateReply();
            response.Attachments = new List<Attachment>() { attachment };
            return response;
        }

        /// <summary>
        /// Helper function to update greeting state with entities returned by LUIS.
        /// </summary>
        /// <param name="luisResult">LUIS recognizer <see cref="RecognizerResult"/>.</param>
        /// <param name="turnContext">A <see cref="ITurnContext"/> containing all the data needed
        /// for processing this conversation turn.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        private async Task UpdateGreetingState(RecognizerResult luisResult, ITurnContext turnContext)
        {
            if (luisResult.Entities != null && luisResult.Entities.HasValues)
            {
                // Get latest GreetingState
                var greetingState = await _greetingStateAccessor.GetAsync(turnContext, () => new GreetingState());
                var entities = luisResult.Entities;

                // Supported LUIS Entities
                string[] userNameEntities = { "userName", "userName_patternAny" };
                string[] userLocationEntities = { "userLocation", "userLocation_patternAny" };

                // Update any entities
                // Note: Consider a confirm dialog, instead of just updating.
                foreach (var name in userNameEntities)
                {
                    // Check if we found valid slot values in entities returned from LUIS.
                    if (entities[name] != null)
                    {
                        // Capitalize and set new user name.
                        var newName = (string)entities[name][0];
                        greetingState.Name = char.ToUpper(newName[0]) + newName.Substring(1);
                        break;
                    }
                }

                foreach (var city in userLocationEntities)
                {
                    if (entities[city] != null)
                    {
                        // Capitalize and set new city.
                        var newCity = (string)entities[city][0];
                        greetingState.City = char.ToUpper(newCity[0]) + newCity.Substring(1);
                        break;
                    }
                }

                // Set the new values into state.
                await _greetingStateAccessor.SetAsync(turnContext, greetingState);
            }
        }
    }
}
