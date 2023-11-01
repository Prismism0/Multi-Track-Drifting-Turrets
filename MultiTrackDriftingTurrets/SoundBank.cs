using System.IO;

namespace MultiTrackDriftingTurrets
{
	public static class SoundBank
	{
		//The folder where your SoundBanks are, it is required for them to be in a folder.
		public const string soundBankFolder = "SoundBanks";
		public const string soundBankFileName = "DEJAVU_Soundbank.bnk";
		public const string soundBankName = "MySoundBank";
		public static string SoundBankDirectory
		{
			get
			{
				return Path.Combine(Path.GetDirectoryName(MultiTrackDriftingTurrets.PInfo.Location), soundBankFolder);
			}
		}

		public static void Init()
		{
			var akResult = AkSoundEngine.AddBasePath(SoundBankDirectory);
			if (akResult == AKRESULT.AK_Success)
			{
				Log.Info($"Added bank base path : {SoundBankDirectory}");
			}
			else
			{
				Log.Error(
					$"Error adding base path : {SoundBankDirectory} " +
					$"Error code : {akResult}");
			}

			akResult = AkSoundEngine.LoadBank(soundBankFileName, out uint _soundBankId);
			if (akResult == AKRESULT.AK_Success)
			{
				Log.Info($"Added bank : {soundBankFileName}");
			}
			else
			{
				Log.Error(
					$"Error loading bank : {soundBankFileName} " +
					$"Error code : {akResult}");
			}
		}
	}
}
