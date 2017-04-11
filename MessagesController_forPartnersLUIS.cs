using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;
using Microsoft.Bot.Builder.Dialogs;
using DEFAULT;
using System.Collections.Generic;

namespace forPartners
{
    [BotAuthentication]
    public class MessagesController : ApiController
    {
        #region 公開すべきでないコード ★適宜修正ください。
        /// <summary>
        /// LUIS エンドポイント "q=" で終了していることが前提。
        /// </summary>
        const string MyLUISEndpoint = "https://westus.api.cognitive.microsoft.com/luis/v2.0/apps/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx?subscription-key=xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx&verbose=true&q=";
        #endregion
        /// <summary>
        /// LUIS のレスポンスのスコアが、この値以上の場合のみ有効な intent と見做すための閾値。
        /// </summary>
        const float THRESHOLD = 0.5F;
        /// <summary>
        /// LUIS のエンドポイントにアクセスするためのクライアント
        /// </summary>
        HttpClient luisHttpClient;

        /// <summary>
        /// LUIS アプリで設定している Intents に相当する Enum
        /// </summary>
        private enum IntentEnum { Debug, GetMyAcvitities, GetMyOpportunities, GetMyTodayAccounts, GetMyTodayActivities, GetMyTodayOpportunities, GetNews, Hello, No, None, ReportActivity, SendThisActivityToManager, Yes };

        /// <summary>
        /// 会話が今どのようなモードであるのかを定義する Enum。
        /// 複数回の返信にて成立するような Intent を処理するときに使用する。
        /// </summary>
        private enum ModeEnum { ReportActivity, SendThisActivityToManager, Other};
        /// <summary>
        /// POST: api/Messages
        /// Receive a message from a user and reply to it
        /// </summary>
        public async Task<HttpResponseMessage> Post([FromBody]Activity activity)
        {
            if (activity.Type == ActivityTypes.Message)
            {
                ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));

                await ProcessActivities(activity, connector);
            }
            else
            {
                HandleSystemMessage(activity);
            }
            var response = Request.CreateResponse(HttpStatusCode.OK);
            return response;
        }
        /// <summary>
        /// 会話のモードなどから様々な処理や返信をする。
        /// </summary>
        /// <param name="activity"></param>
        /// <param name="connector"></param>
        /// <returns></returns>
        private async Task ProcessActivities(Activity activity, ConnectorClient connector)
        {
            StateClient stateClient = activity.GetStateClient();
            BotData userData = await stateClient.BotState.GetUserDataAsync(activity.ChannelId, activity.From.Id);
            ModeEnum currentMode = userData.GetProperty<ModeEnum>("mode");
            ModeEnum nextMode = ModeEnum.Other;

            switch (currentMode)
            {
                // 報告メモを残すモードの処理
                case ModeEnum.ReportActivity:
                    nextMode = await ProcessReportActivityMode(activity, connector);
                    break;
                // 上司にメールを送信するモードの処理
                case ModeEnum.SendThisActivityToManager:
                    nextMode = await ProcessSendThisActivityToManagerMode(activity, connector);
                    break;
                // その他のモードでは、LUIS で意図を汲み取る処理
                default:
                    nextMode = await ProcessOtherModeWithLUIS(activity, connector);
                    break;
            }

            userData.SetProperty<ModeEnum>("mode", nextMode);
            await stateClient.BotState.SetUserDataAsync(activity.ChannelId, activity.From.Id, userData);
        }
        /// <summary>
        /// モード ReportActivity の処理をする。
        /// </summary>
        /// <returns></returns>
        private async Task<ModeEnum> ProcessReportActivityMode(Activity activity, ConnectorClient connector)
        {
            Activity reply = activity.CreateReply("活動報告を登録しました。");
            await connector.Conversations.ReplyToActivityAsync(reply);

            return ModeEnum.Other;
        }
        /// <summary>
        /// モード SendThisActivityToManager の処理をする。
        /// </summary>
        /// <returns></returns>
        private async Task<ModeEnum> ProcessSendThisActivityToManagerMode(Activity activity, ConnectorClient connector)
        {
            string luisResultString = await GetLUISResultString(activity);
            if (!string.IsNullOrEmpty(luisResultString))
            {
                try
                {
                    Activity reply = null;
                    LUISResponse luisResponse = JsonConvert.DeserializeObject<LUISResponse>(luisResultString);
                    string intent = luisResponse.topScoringIntent.intent;
                    float score = float.Parse(luisResponse.topScoringIntent.score);

                    if (activity.Text.IndexOf("はい") == 0 ||
                        score >= THRESHOLD && intent == IntentEnum.Yes.ToString())
                    {
                        reply = activity.CreateReply("上司にメールを送信しました。");
                    }
                    else
                    {
                        reply = activity.CreateReply("送信しません。");
                    }

                    if (reply != null) await connector.Conversations.ReplyToActivityAsync(reply);
                }
                catch (Exception e)
                {
                    Activity reply = activity.CreateReply($"Error: {e.Message}");
                    await connector.Conversations.ReplyToActivityAsync(reply);
                }
            }

            return ModeEnum.Other;
        }
        /// <summary>
        /// 特定のモードではない場合の処理をする。そのために、LUIS 関係の処理および返信をする。
        /// </summary>
        /// <param name="activity"></param>
        /// <param name="connector"></param>
        /// <returns></returns>
        private async Task<ModeEnum> ProcessOtherModeWithLUIS(Activity activity, ConnectorClient connector)
        {
            ModeEnum nextMode = ModeEnum.Other;

            string luisResultString = await GetLUISResultString(activity);
            if (!string.IsNullOrEmpty(luisResultString))
            {
                try
                {
                    Activity reply = null;
                    LUISResponse luisResponse = JsonConvert.DeserializeObject<LUISResponse>(luisResultString);
                    string intent = luisResponse.topScoringIntent.intent;
                    float score = float.Parse(luisResponse.topScoringIntent.score);

                    if (score >= THRESHOLD)
                    {
                        if (intent == IntentEnum.Debug.ToString())
                        {
                            reply = activity.CreateReply(
                                $"activity.From.Id: {activity.From.Id}\n\n" +
                                $"activity.From.Name: {activity.From.Name}\n\n" +
                                $"activity.ChannelId: {activity.ChannelId}\n\n"
                            );
                        }
                        else if (intent == IntentEnum.GetMyAcvitities.ToString())
                        {
                            reply = activity.CreateReply("過去の訪問の一覧はこちらです。");
                        }
                        else if (intent == IntentEnum.GetMyOpportunities.ToString())
                        {
                            reply = activity.CreateReply("営業案件の一覧はこちらです。");
                        }
                        else if (intent == IntentEnum.GetMyTodayAccounts.ToString())
                        {
                            reply = CreateMyTodayAccountsReply(activity, "本日、訪問を予定しているお客様の一覧はこちらです。");
                        }
                        else if (intent == IntentEnum.GetMyTodayActivities.ToString())
                        {
                            reply = CreateMyTodayActivitiesReply(activity, "本日予定している訪問の一覧はこちらです。");
                        }
                        else if (intent == IntentEnum.GetMyTodayOpportunities.ToString())
                        {
                            reply = activity.CreateReply("本日予定している訪問に関する営業案件の一覧はこちらです。");
                        }
                        else if (intent == IntentEnum.GetNews.ToString())
                        {
                            reply = activity.CreateReply("今日のニュースは特にございません。");
                        }
                        else if (intent == IntentEnum.Hello.ToString())
                        {
                            reply = activity.CreateReply($"{GetUserFullname(activity)}さん、こんにちは");
                        }
                        else if (intent == IntentEnum.No.ToString())
                        {
                            reply = activity.CreateReply("承知しました。");
                        }
                        else if (intent == IntentEnum.ReportActivity.ToString())
                        {
                            reply = activity.CreateReply("訪問**「港コンピュータ様AR025打ち合わせ」**に関する活動報告を残します。");
                            nextMode = ModeEnum.ReportActivity;
                        }
                        else if (intent == IntentEnum.SendThisActivityToManager.ToString())
                        {
                            reply = activity.CreateReply($"上司の{GetManagerFullname(activity)}さんに、訪問**「港コンピュータ様AR025打ち合わせ」**に関する活動報告について電子メール送信してよいですか？");
                            await connector.Conversations.ReplyToActivityAsync(reply);
                            reply = CreateYesNoReply(activity, "");
                            nextMode = ModeEnum.SendThisActivityToManager;
                        }
                        else if (intent == IntentEnum.Yes.ToString())
                        {
                            reply = activity.CreateReply("承知しました。");
                        }
                        else
                        {
                            reply = activity.CreateReply("あいにく、用意している返信はございません。");
                        }
                    }
                    else
                    {
                        reply = activity.CreateReply("すみませんが、内容を理解できません。");
                    }

                    if (reply != null) await connector.Conversations.ReplyToActivityAsync(reply);
                }
                catch (Exception e)
                {
                    Activity reply = activity.CreateReply($"Error: {e.Message}");
                    await connector.Conversations.ReplyToActivityAsync(reply);
                }
            }

            return nextMode;
        }
        /// <summary>
        /// Activity の Text を渡して、LUS アプリからの戻り値を得る。
        /// </summary>
        /// <param name="activity"></param>
        /// <returns></returns>
        private async Task<string> GetLUISResultString(Activity activity)
        {
            if (luisHttpClient == null) luisHttpClient = new HttpClient(new HttpClientHandler());
            HttpResponseMessage httpResponse = await luisHttpClient.GetAsync(MyLUISEndpoint + activity.Text);
            string resultString = await httpResponse.Content.ReadAsStringAsync();
            return resultString;
        }

        private Activity CreateMyTodayAccountsReply(Activity activity, string title)
        {
            Activity replyMessage = activity.CreateReply();
            replyMessage.Attachments = new List<Attachment>();
            List<CardAction> cardButtons = new List<CardAction>();
            cardButtons.Add(new CardAction()
            {
                Title = "コントソ製薬",
                Value = "コントソ製薬",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "ファブリカム",
                Value = "ファブリカム",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "港コンピュータ",
                Value = "港コンピュータ",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "フォース コーヒー",
                Value = "フォース コーヒー",
                Type = "imBack"
            });

            HeroCard heroCard = new HeroCard()
            {
                Subtitle = title,
                Buttons = cardButtons
            };
            Attachment heroAttachment = heroCard.ToAttachment();
            replyMessage.Attachments.Add(heroAttachment);
            return replyMessage;
        }

        private Activity CreateMyTodayActivitiesReply(Activity activity, string title)
        {
            Activity replyMessage = activity.CreateReply();
            replyMessage.Attachments = new List<Attachment>();
            List<CardAction> cardButtons = new List<CardAction>();
            cardButtons.Add(new CardAction()
            {
                Title = "コントソ製薬様TX083アップグレード (10:00)",
                Value = "コントソ製薬様TX083アップグレード",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "ファブリカム様ご挨拶 田中部長 (13:30)",
                Value = "ファブリカム様ご挨拶 田中部長",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "港コンピュータ様AR025打ち合わせ (15:00)",
                Value = "港コンピュータ様AR025打ち合わせ",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "見積もりの提示、フォースコーヒー様 (17:30)",
                Value = "見積もりの提示、フォースコーヒー様",
                Type = "imBack"
            });

            HeroCard heroCard = new HeroCard()
            {
                Subtitle = title,
                Buttons = cardButtons
            };
            Attachment heroAttachment = heroCard.ToAttachment();
            replyMessage.Attachments.Add(heroAttachment);
            return replyMessage;
        }
        private Activity CreateYesNoReply(Activity activity, string title)
        {
            Activity replyMessage = activity.CreateReply();
            replyMessage.Attachments = new List<Attachment>();
            List<CardAction> cardButtons = new List<CardAction>();
            cardButtons.Add(new CardAction()
            {
                Title = "はい",
                Value = "はい",
                Type = "imBack"
            });
            cardButtons.Add(new CardAction()
            {
                Title = "いいえ",
                Value = "いいえ",
                Type = "imBack"
            });

            HeroCard heroCard = new HeroCard()
            {
                Subtitle = title,
                Buttons = cardButtons
            };
            Attachment heroAttachment = heroCard.ToAttachment();
            replyMessage.Attachments.Add(heroAttachment);
            return replyMessage;
        }
        private string GetUserFullname(Activity activity)
        {
            // 適宜修正ください。
            switch (activity.From.Name)
            {
                case "keiji_ms_com":
                case "Keiji Inoue":
                    return "井上 圭司";
                default:
                    return "営業 太郎";
            }
        }
        private string GetManagerFullname(Activity activity)
        {
            // 適宜修正ください。
            switch (activity.From.Name)
            {
                case "keiji_ms_com":
                case "Keiji Inoue":
                    return "田川 登久也";
                default:
                    return "営業 部長";
            }
        }
        private Activity HandleSystemMessage(Activity message)
        {
            if (message.Type == ActivityTypes.DeleteUserData)
            {
                // Implement user deletion here
                // If we handle user deletion, return a real message
            }
            else if (message.Type == ActivityTypes.ConversationUpdate)
            {
                // Handle conversation state changes, like members being added and removed
                // Use Activity.MembersAdded and Activity.MembersRemoved and Activity.Action for info
                // Not available in all channels
            }
            else if (message.Type == ActivityTypes.ContactRelationUpdate)
            {
                // Handle add/remove from contact lists
                // Activity.From + Activity.Action represent what happened
            }
            else if (message.Type == ActivityTypes.Typing)
            {
                // Handle knowing tha the user is typing
            }
            else if (message.Type == ActivityTypes.Ping)
            {
            }

            return null;
        }
    }
}