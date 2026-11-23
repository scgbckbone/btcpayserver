using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NBitcoin;

namespace BTCPayServer.Plugins.Wallets.Views.ViewModels
{
    public class WalletPSBTViewModel : WalletPSBTReadyViewModel
    {
        public const int MaxPSBTLength = 500 * 1024;

        public string CryptoCode { get; set; }
        public string Decoded { get; set; }
        string _FileName;
        public string PSBTHex { get; set; }
        public bool NBXSeedAvailable { get; set; }

        public string FileName
        {
            get
            {
                return string.IsNullOrEmpty(_FileName) ? "psbt-export.psbt" : _FileName;
            }
            set
            {
                _FileName = value;
            }
        }
        [Display(Name = "PSBT content")]
        public string PSBT { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// Set when this PSBT was read from a signed transaction, so the page can say
        /// so: there is nothing left to sign in it.
        /// </summary>
        public bool ProvidedTransaction { get; set; }

        /// <summary>
        /// A signed transaction, hex-encoded, read from its own box. It is not a PSBT
        /// and it is deliberately not accepted where one is asked for: a signer that
        /// finalizes on the device returns this instead, with nothing left to sign.
        /// </summary>
        [Display(Name = "Signed transaction")]
        public string SignedTransaction { get; set; }

        [Display(Name = "Upload signed transaction from file…")]
        public IFormFile UploadedTransactionFile { get; set; }

        public bool InvalidTransaction { get; set; }

        /// <summary>
        /// The transaction this PSBT would broadcast, hex-encoded, or null while the
        /// PSBT is still missing signatures. This is what a block explorer, a node's
        /// sendrawtransaction or another wallet's broadcast screen takes.
        /// </summary>
        public string TransactionHex { get; set; }
        public string TransactionId { get; set; }

        [Display(Name = "Upload PSBT from file…")]
        public IFormFile UploadedPSBTFile { get; set; }


        #nullable enable
        public async Task<PSBT?> GetPSBT(Network? network, ModelStateDictionary modelState)
        {
            var psbt = await GetPSBTCore(network, modelState);
            if (psbt != null)
            {
                Decoded = psbt.ToString();
                PSBTHex = psbt.ToHex();
                PSBT = psbt.ToBase64();
                if (SigningContext is null)
                    SigningContext = new SigningContextModel(psbt);
                else
                    SigningContext.PSBT = psbt.ToBase64();
                SetTransaction(psbt);
            }
            return psbt;
        }
        public bool InvalidPSBT { get; set; }

        async Task<PSBT?> GetPSBTCore(Network? network, ModelStateDictionary modelState)
        {
            if (UploadedPSBTFile != null)
            {
                if (UploadedPSBTFile.Length > MaxPSBTLength)
                {
                    modelState.AddModelError(nameof(PSBT), "The PSBT is too large.");
                    InvalidPSBT = true;
                    return null;
                }

                try
                {
                    byte[] bytes = new byte[UploadedPSBTFile.Length];
                    await using (var stream = UploadedPSBTFile.OpenReadStream())
                    {
                        await stream.ReadAsync(bytes, 0, (int)UploadedPSBTFile.Length);
                    }
                    if (network is null)
                        return null;
                    return NBitcoin.PSBT.Load(bytes, network);
                }
                catch (Exception ex)
                {
                    using var stream = new StreamReader(UploadedPSBTFile.OpenReadStream());
                    PSBT = await stream.ReadToEndAsync();
                    modelState.Remove(nameof(PSBT));
                    modelState.AddModelError(nameof(PSBT), ex.Message);
                    InvalidPSBT = true;
                }
            }
            if (SigningContext != null && !string.IsNullOrEmpty(SigningContext.PSBT))
            {
                PSBT = SigningContext.PSBT;
                modelState.Remove(nameof(PSBT));
                InvalidPSBT = false;
            }
            if (PSBT?.Length > MaxPSBTLength)
            {
                modelState.AddModelError(nameof(PSBT), "The PSBT is too large.");
                InvalidPSBT = true;
                return null;
            }
            if (!string.IsNullOrEmpty(PSBT))
            {
                try
                {
                    InvalidPSBT = false;
                    if (network is null)
                        return null;
                    return NBitcoin.PSBT.Parse(PSBT, network);
                }
                catch (Exception ex) when (!InvalidPSBT)
                {
                    modelState.AddModelError(nameof(PSBT),
                        network is not null && Transaction.TryParse(PSBT.Trim(), network, out _)
                            ? "This is a transaction, not a PSBT. Use the signed transaction box for it."
                            : ex.Message);
                    InvalidPSBT = true;
                }
            }
            return null;
        }

        /// <summary>
        /// Read the signed transaction from its own box, as the PSBT that means the
        /// same thing, leaving everything downstream unable to tell the two apart.
        /// </summary>
        /// <remarks>
        /// Its own box, and not a second thing the PSBT box will quietly swallow: a
        /// transaction cannot be signed, combined or updated, so a reader who pastes
        /// one where a PSBT is asked for is told which box it belongs in instead.
        /// The Greenfield broadcast endpoint has always accepted either.
        /// </remarks>
        public async Task<bool> ReadSignedTransaction(Network? network, ModelStateDictionary modelState)
        {
            Transaction? transaction = null;
            if (UploadedTransactionFile is not null)
            {
                if (UploadedTransactionFile.Length > MaxPSBTLength)
                {
                    modelState.AddModelError(nameof(SignedTransaction), "The transaction is too large.");
                    InvalidTransaction = true;
                    return false;
                }
                var bytes = new byte[UploadedTransactionFile.Length];
                await using (var stream = UploadedTransactionFile.OpenReadStream())
                {
                    await stream.ReadAsync(bytes, 0, (int)UploadedTransactionFile.Length);
                }
                if (network is null)
                    return false;
                // Hex in a file is what a signer writes — Coldcard's .txn — and the
                // transaction's own bytes are what another one writes.
                if (!TryLoadTransaction(bytes, network, out transaction))
                    SignedTransaction = Encoding.UTF8.GetString(bytes).Trim();
            }
            if (transaction is null)
            {
                if (string.IsNullOrWhiteSpace(SignedTransaction))
                {
                    modelState.AddModelError(nameof(SignedTransaction), "Provide a signed transaction.");
                    InvalidTransaction = true;
                    return false;
                }
                if (SignedTransaction.Length > MaxPSBTLength)
                {
                    modelState.AddModelError(nameof(SignedTransaction), "The transaction is too large.");
                    InvalidTransaction = true;
                    return false;
                }
                if (network is null)
                    return false;
                if (!Transaction.TryParse(SignedTransaction.Trim(), network, out transaction))
                {
                    modelState.AddModelError(nameof(SignedTransaction),
                        NBitcoin.PSBT.TryParse(SignedTransaction.Trim(), network, out _)
                            ? "This is a PSBT, not a transaction. Use the PSBT box for it."
                            : "This is not a transaction.");
                    InvalidTransaction = true;
                    return false;
                }
            }
            PSBT psbt;
            try
            {
                psbt = transaction.ToPSBT(network!);
            }
            catch (Exception ex)
            {
                modelState.AddModelError(nameof(SignedTransaction), ex.Message);
                InvalidTransaction = true;
                return false;
            }
            // From here the PSBT is the only thing anyone reads, and GetPSBT puts the
            // rest of the model together from it as it would for a pasted PSBT.
            PSBT = psbt.ToBase64();
            SignedTransaction = null;
            ProvidedTransaction = true;
            modelState.Remove(nameof(this.PSBT));
            return true;
        }

        static bool TryLoadTransaction(byte[] bytes, Network network, out Transaction? transaction)
        {
            try
            {
                transaction = Transaction.Load(bytes, network);
                return true;
            }
            catch (Exception)
            {
                transaction = null;
                return false;
            }
        }

        void SetTransaction(PSBT psbt)
        {
            // Available exactly when the PSBT is complete, whether or not it arrived
            // finalized: that is the test for "there is something to broadcast".
            var finalized = psbt.IsAllFinalized() ? psbt : psbt.Clone();
            if (!finalized.IsAllFinalized() && !finalized.TryFinalize(out _))
                return;
            var transaction = finalized.ExtractTransaction();
            TransactionHex = transaction.ToHex();
            TransactionId = transaction.GetHash().ToString();
        }
    }
}
