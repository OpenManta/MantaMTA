﻿using MantaMTA.Core.Client.BO;
using MantaMTA.Core.DNS;
using MantaMTA.Core.RabbitMq;
using MantaMTA.Core.Smtp;
using MantaMTA.Core.VirtualMta;
using System;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace MantaMTA.Core.Client
{
	/// <summary>
	/// MessageSender sends Emails to other servers from the Queue.
	/// </summary>
	public class MessageSender : IStopRequired
	{
		#region Singleton
		/// <summary>
		/// The Single instance of this class.
		/// </summary>
		private static MessageSender _Instance = new MessageSender();
		
		/// <summary>
		/// Instance of the MessageSender class.
		/// </summary>
		public static MessageSender Instance
		{
			get
			{
				return MessageSender._Instance;
			}
		}

		private MessageSender()
		{
			MantaCoreEvents.RegisterStopRequiredInstance(this);
		}
		#endregion

		/// <summary>
		/// If TRUE then request for client to stop has been made.
		/// </summary>
		private bool _IsStopping = false;

		/// <summary>
		/// IStopRequired method. Will be called by MantaCoreEvents on stopping of MTA.
		/// </summary>
		public void Stop()
		{
			this._IsStopping = true;
		}


		public void Start()
		{
			Thread t = new Thread(new ThreadStart(() => {
				// Dictionary will hold a single int for each running task. The int means nothing.
				ConcurrentDictionary<Guid, int> runningTasks = new ConcurrentDictionary<Guid, int>();

				while(!_IsStopping)
				{
					MtaQueuedMessage toSend = RabbitMqOutboundQueueManager.Dequeue();
					if (toSend == null)
					{
						Thread.Sleep(100);
						continue;
					}
					SendMessageAsync(toSend).Wait();
					RabbitMqOutboundQueueManager.Ack(toSend);
				}
			}));
			t.Start();
		}


		private async Task<bool> SendMessageAsync(MtaQueuedMessage msg)
		{
			// Check that the message next attempt after has passed.
			if (msg.AttemptSendAfterUtc > DateTime.UtcNow)
			{
				RabbitMqOutboundQueueManager.Enqueue(msg);
				return false;
			}

			bool result;
			// Check the message hasn't timed out. If it has don't attempt to send it.
			// Need to do this here as there may be a massive backlog on the server
			// causing messages to be waiting for ages after there AttemptSendAfter
			// before picking up. The MAX_TIME_IN_QUEUE should always be enforced.
			if (msg.AttemptSendAfterUtc - msg.QueuedTimestampUtc > new TimeSpan(0, MtaParameters.MtaMaxTimeInQueue, 0))
			{
				await msg.HandleDeliveryFailAsync("Timed out in queue.", null, null);
				result = false;
			}
			else
			{
				MailAddress mailAddress = new MailAddress(msg.RcptTo[0]);
				MailAddress mailFrom = new MailAddress(msg.MailFrom);
				MXRecord[] mXRecords = DNSManager.GetMXRecords(mailAddress.Host);
				// If mxs is null then there are no MX records.
				if (mXRecords == null || mXRecords.Length < 1)
				{
					await msg.HandleDeliveryFailAsync("550 Domain Not Found.", null, null);
					result = false;
				}
				else
				{
					// The IP group that will be used to send the queued message.
					VirtualMtaGroup virtualMtaGroup = VirtualMtaManager.GetVirtualMtaGroup(msg.VirtualMTAGroupID);
					VirtualMTA sndIpAddress = virtualMtaGroup.GetVirtualMtasForSending(mXRecords[0]);

					SmtpOutboundClientDequeueResponse dequeueResponse = await SmtpClientPool.Instance.DequeueAsync(sndIpAddress, mXRecords);
					switch (dequeueResponse.DequeueResult)
					{
						case SmtpOutboundClientDequeueAsyncResult.Success:
						case SmtpOutboundClientDequeueAsyncResult.NoMxRecords:
						case SmtpOutboundClientDequeueAsyncResult.FailedToAddToSmtpClientQueue:
						case SmtpOutboundClientDequeueAsyncResult.Unknown:
							break; // Don't need to do anything for these results.
						case SmtpOutboundClientDequeueAsyncResult.FailedToConnect:
							await msg.HandleDeliveryDeferralAsync("Failed to connect", sndIpAddress, mXRecords[0]);
							break;
						case SmtpOutboundClientDequeueAsyncResult.ServiceUnavalible:
							await msg.HandleServiceUnavailableAsync(sndIpAddress);
							break;
						case SmtpOutboundClientDequeueAsyncResult.Throttled:
							await msg.HandleDeliveryThrottleAsync(sndIpAddress, mXRecords[0]);
							break;
						case SmtpOutboundClientDequeueAsyncResult.FailedMaxConnections:
							msg.AttemptSendAfterUtc = DateTime.UtcNow.AddSeconds(2);
							break;
					}

					SmtpOutboundClient smtpClient = dequeueResponse.SmtpOutboundClient;

					// If no client was dequeued then we can't currently send.
					// This is most likely a max connection issue. Return false but don't
					// log any deferal or throttle.
					if (smtpClient == null)
					{
						result = false;
					}
					else
					{
						try
						{
							Action<string> failedCallback = delegate(string smtpResponse)
							{
								// If smtpRespose starts with 5 then perm error should cause fail
								if (smtpResponse.StartsWith("5"))
									msg.HandleDeliveryFailAsync(smtpResponse, sndIpAddress, smtpClient.MXRecord).Wait();
								else
								{
									// If the MX is actively denying use service access, SMTP code 421 then we should inform
									// the ServiceNotAvailableManager manager so it limits our attepts to this MX to 1/minute.
									if (smtpResponse.StartsWith("421"))
									{
										ServiceNotAvailableManager.Add(smtpClient.SmtpStream.LocalAddress.ToString(), smtpClient.MXRecord.Host, DateTime.UtcNow);
										msg.HandleDeliveryDeferral(smtpResponse, sndIpAddress, smtpClient.MXRecord, true);
									}
									else
									{
										// Otherwise message is deferred
										msg.HandleDeliveryDeferral(smtpResponse, sndIpAddress, smtpClient.MXRecord, false);
									}
								}
								throw new SmtpTransactionFailedException();
							};
							// Run each SMTP command after the last.
							await smtpClient.ExecHeloOrRsetAsync(failedCallback);
							await smtpClient.ExecMailFromAsync(mailFrom, failedCallback);
							await smtpClient.ExecRcptToAsync(mailAddress, failedCallback);
							await smtpClient.ExecDataAsync(msg.Message, failedCallback);
							SmtpClientPool.Instance.Enqueue(smtpClient);
							await msg.HandleDeliverySuccessAsync(sndIpAddress, smtpClient.MXRecord);
							result = true;
						}
						catch (SmtpTransactionFailedException)
						{
							// Exception is thrown to exit transaction, logging of deferrals/failers already handled.
							result = false;
						}
						catch (Exception ex)
						{
							Logging.Error("MessageSender error.", ex);
							if (msg != null)
								msg.HandleDeliveryDeferral("Connection was established but ended abruptly.", sndIpAddress, smtpClient.MXRecord, false);
							result = false;
						}
						finally
						{
							if (smtpClient != null)
							{
								smtpClient.IsActive = false;
								smtpClient.LastActive = DateTime.UtcNow;
							}
						}
					}
				}
			}
			return result;
		}

		/// <summary>
		/// Exception is used to halt SMTP transaction if the server responds with unexpected code.
		/// </summary>
		[Serializable]
		private class SmtpTransactionFailedException : Exception { }
	}

	
}
