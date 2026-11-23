using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Wallets.Views.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Tests
{
    public class PSBTTests(ITestOutputHelper helper) : UnitTestBase(helper)
    {
        [Fact]
        [Trait("Fast", "Fast")]
        public async Task RejectsOversizedTextualPSBTBeforeParsing()
        {
            var modelState = new ModelStateDictionary();
            var vm = new WalletPSBTViewModel
            {
                PSBT = new string('A', 500 * 1024 + 1)
            };

            Assert.Null(await vm.GetPSBT(Network.RegTest, modelState));
            Assert.False(modelState.IsValid);
            Assert.Contains(modelState["PSBT"].Errors, error => error.ErrorMessage == "The PSBT is too large.");
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public void SeedSigningLinkOnlyCarriesTheTransactionToSign()
        {
            // The seed signing GET touches no service, so an instance without dependencies is enough.
            var controller = new UIWalletsController(null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null);

            var result = Assert.IsType<ViewResult>(controller.SignWithSeed(null, new SigningContextModel
            {
                PSBT = "the-psbt",
                EnforceLowR = false,
                OriginalPSBT = "the-original-psbt",
                PendingTransactionId = "the-pending-transaction",
                PayJoinBIP21 = "the-bip21",
                ChangeAddress = "the-change-address",
                Comment = "the-comment",
                BalanceChangeFromReplacement = long.MaxValue
            }, null, null));

            var signingContext = Assert.IsType<SignWithSeedViewModel>(result.Model).SigningContext;
            Assert.Equal("the-psbt", signingContext.PSBT);
            Assert.False(signingContext.EnforceLowR);
            Assert.Null(signingContext.OriginalPSBT);
            Assert.Null(signingContext.PendingTransactionId);
            Assert.Null(signingContext.PayJoinBIP21);
            Assert.Null(signingContext.ChangeAddress);
            Assert.Null(signingContext.Comment);
            Assert.Equal(0, signingContext.BalanceChangeFromReplacement);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task ReadsASignedTransactionWhereAPSBTIsExpected()
        {
            // What a signer that finalizes on the device hands back: no PSBT, a
            // transaction. The Greenfield broadcast endpoint has always taken either.
            var network = Network.RegTest;
            var signed = SignedTransaction(network, out var coin);

            var vm = new WalletPSBTViewModel { SignedTransaction = signed.ToHex() };
            var modelState = new ModelStateDictionary();
            Assert.True(await vm.ReadSignedTransaction(network, modelState));
            var psbt = await vm.GetPSBT(network, modelState);

            Assert.NotNull(psbt);
            Assert.True(vm.ProvidedTransaction);
            Assert.True(psbt.IsAllFinalized());
            // Nothing is lost on the way through: this is the transaction that will
            // be broadcast, byte for byte, and the page offers exactly it.
            Assert.Equal(signed.ToHex(), psbt.ExtractTransaction().ToHex());
            Assert.Equal(signed.ToHex(), vm.TransactionHex);
            Assert.Equal(signed.GetHash().ToString(), vm.TransactionId);
            Assert.Equal(psbt.ToBase64(), vm.SigningContext.PSBT);
            // A transaction does not carry what its inputs were worth, so the fee is
            // not known here; the controller asks NBXplorer for the outpoints instead.
            Assert.All(psbt.Inputs, input => Assert.Null(input.GetTxOut()));
            Assert.Equal(coin.Outpoint, psbt.Inputs[0].PrevOut);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task KeepsEveryShapeOfSignatureIntact()
        {
            // The signature of a legacy input lives in the scriptSig, a native segwit
            // one in the witness, and a nested segwit one in both. Reading a transaction
            // moves those fields into a PSBT, so what comes back out has to be the same
            // bytes for every shape, not only the one a test happened to build.
            var network = Network.RegTest;
            var keys = new[] { new Key(), new Key(), new Key() };
            var scriptPubKeys = new[]
            {
                keys[0].PubKey.GetAddress(ScriptPubKeyType.Legacy, network).ScriptPubKey,
                keys[1].PubKey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey,
                keys[2].PubKey.GetAddress(ScriptPubKeyType.SegwitP2SH, network).ScriptPubKey
            };
            var funding = network.CreateTransaction();
            foreach (var scriptPubKey in scriptPubKeys)
                funding.Outputs.Add(Money.Coins(1.0m), scriptPubKey);
            var coins = funding.Outputs.AsCoins().ToArray();

            var signed = network.CreateTransactionBuilder()
                .AddCoins(coins[0], coins[1])
                .AddCoin(coins[2].ToScriptCoin(keys[2].PubKey.WitHash.ScriptPubKey))
                .AddKeys(keys)
                .Send(new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network), Money.Coins(2.5m))
                .SetChange(scriptPubKeys[1])
                .SendEstimatedFees(new FeeRate(10m))
                .BuildTransaction(sign: true);
            Assert.Equal(3, signed.Inputs.Count);
            Assert.Equal(2, signed.Inputs.Count(input => input.ScriptSig.Length > 0));
            Assert.Equal(2, signed.Inputs.Count(input => input.WitScript != WitScript.Empty));

            var vm = new WalletPSBTViewModel { SignedTransaction = signed.ToHex() };
            var modelState = new ModelStateDictionary();
            Assert.True(await vm.ReadSignedTransaction(network, modelState));
            var psbt = await vm.GetPSBT(network, modelState);

            Assert.NotNull(psbt);
            Assert.True(psbt.IsAllFinalized());
            Assert.Equal(signed.ToHex(), psbt.ExtractTransaction().ToHex());
            Assert.Equal(signed.ToHex(), vm.TransactionHex);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task ReadsASignedTransactionFromAnUploadedFile()
        {
            // Coldcard writes the finalized transaction as hex in a .txn file, and
            // another signer may write its bytes; both arrive through the upload box.
            var network = Network.RegTest;
            var signed = SignedTransaction(network, out _);

            foreach (var content in new[] { Encoding.ASCII.GetBytes(signed.ToHex() + "\n"), signed.ToBytes() })
            {
                var vm = new WalletPSBTViewModel
                {
                    UploadedTransactionFile = new FormFile(new MemoryStream(content), 0, content.Length, "file", "signed.txn")
                };
                var modelState = new ModelStateDictionary();
                Assert.True(await vm.ReadSignedTransaction(network, modelState));
                var psbt = await vm.GetPSBT(network, modelState);

                Assert.NotNull(psbt);
                Assert.True(vm.ProvidedTransaction);
                Assert.Equal(signed.ToHex(), psbt.ExtractTransaction().ToHex());
            }
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task KeepsEachBoxToItsOwnFormat()
        {
            // Neither box quietly swallows the other's format: a transaction cannot be
            // signed, combined or updated, so a reader who pastes one where a PSBT is
            // asked for is told which box it belongs in rather than left guessing.
            var network = Network.RegTest;
            var signed = SignedTransaction(network, out _);
            var stripped = signed.Clone();
            stripped.RemoveSignatures();
            var source = PSBT.FromTransaction(stripped, network);

            var psbtBox = new WalletPSBTViewModel { PSBT = signed.ToHex() };
            var psbtBoxState = new ModelStateDictionary();
            Assert.Null(await psbtBox.GetPSBT(network, psbtBoxState));
            Assert.True(psbtBox.InvalidPSBT);
            Assert.False(psbtBox.ProvidedTransaction);
            Assert.Contains(psbtBoxState["PSBT"].Errors,
                error => error.ErrorMessage == "This is a transaction, not a PSBT. Use the signed transaction box for it.");

            var transactionBox = new WalletPSBTViewModel { SignedTransaction = source.ToBase64() };
            var transactionBoxState = new ModelStateDictionary();
            Assert.False(await transactionBox.ReadSignedTransaction(network, transactionBoxState));
            Assert.True(transactionBox.InvalidTransaction);
            Assert.Contains(transactionBoxState["SignedTransaction"].Errors,
                error => error.ErrorMessage == "This is a PSBT, not a transaction. Use the PSBT box for it.");

            var junk = new WalletPSBTViewModel { SignedTransaction = "not a transaction at all" };
            var junkState = new ModelStateDictionary();
            Assert.False(await junk.ReadSignedTransaction(network, junkState));
            Assert.True(junk.InvalidTransaction);
            Assert.Null(junk.TransactionHex);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task OffersNoTransactionWhileSignaturesAreMissing()
        {
            // An unsigned PSBT has nothing to broadcast, so the page must not offer
            // a transaction to download: there is none until it is signed.
            var network = Network.RegTest;
            var key = new Key();
            var scriptPubKey = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey;
            var funding = network.CreateTransaction();
            funding.Outputs.Add(Money.Coins(1.0m), scriptPubKey);
            var unsigned = network.CreateTransactionBuilder()
                .AddCoins(funding.Outputs.AsCoins().First())
                .Send(new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network), Money.Coins(0.5m))
                .SetChange(scriptPubKey)
                .SendEstimatedFees(new FeeRate(10m))
                .BuildTransaction(sign: false);

            var vm = new WalletPSBTViewModel { SignedTransaction = unsigned.ToHex() };
            var modelState = new ModelStateDictionary();
            Assert.True(await vm.ReadSignedTransaction(network, modelState));
            var psbt = await vm.GetPSBT(network, modelState);

            Assert.NotNull(psbt);
            Assert.True(vm.ProvidedTransaction);
            Assert.False(psbt.IsAllFinalized());
            Assert.Null(vm.TransactionHex);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public async Task KeepsReadingPSBTs()
        {
            // The transaction fallback must not change what a PSBT does here.
            var network = Network.RegTest;
            var signed = SignedTransaction(network, out _);
            var stripped = signed.Clone();
            stripped.RemoveSignatures();
            var source = PSBT.FromTransaction(stripped, network);

            foreach (var content in new[] { source.ToBase64(), source.ToHex() })
            {
                var vm = new WalletPSBTViewModel { PSBT = content };
                var psbt = await vm.GetPSBT(network, new ModelStateDictionary());

                Assert.NotNull(psbt);
                Assert.False(vm.ProvidedTransaction);
                Assert.Equal(source.ToBase64(), psbt.ToBase64());
            }
        }

        private static Transaction SignedTransaction(Network network, out Coin coin)
        {
            var key = new Key();
            var scriptPubKey = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey;
            var funding = network.CreateTransaction();
            funding.Outputs.Add(Money.Coins(1.0m), scriptPubKey);
            coin = funding.Outputs.AsCoins().First();
            return network.CreateTransactionBuilder()
                .AddCoins(coin)
                .AddKeys(key)
                .Send(new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network), Money.Coins(0.9m))
                .SetChange(scriptPubKey)
                .SendEstimatedFees(new FeeRate(10m))
                .BuildTransaction(sign: true);
        }

        [Fact]
        [Trait("Fast", "Fast")]
        public void AddsWitnessUtxoToSegwitInputs()
        {
            var network = Network.RegTest;
            var keys = new[] { new Key(), new Key() };
            var witnessScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(
                2,
                keys.Select(key => key.PubKey).ToArray());
            var nativeP2wsh = witnessScript.WitHash.ScriptPubKey;
            var nativeP2wpkh = new Key().PubKey.WitHash.ScriptPubKey;
            var nestedP2wshRedeem = witnessScript.WitHash.ScriptPubKey;
            var nestedP2wsh = nestedP2wshRedeem.Hash.ScriptPubKey;
            var taproot = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
            var futureWitness = PayToWitTemplate.Instance.GenerateScriptPubKey(OpcodeType.OP_2, new byte[32]);
            var legacyP2sh = witnessScript.Hash.ScriptPubKey;

            var previousTransaction = network.CreateTransaction();
            previousTransaction.Outputs.Add(Money.Coins(1.0m), nativeP2wsh);
            previousTransaction.Outputs.Add(Money.Coins(1.1m), nativeP2wpkh);
            previousTransaction.Outputs.Add(Money.Coins(1.2m), nestedP2wsh);
            previousTransaction.Outputs.Add(Money.Coins(1.3m), taproot);
            previousTransaction.Outputs.Add(Money.Coins(1.4m), futureWitness);
            previousTransaction.Outputs.Add(Money.Coins(1.5m), legacyP2sh);

            var spendingTransaction = network.CreateTransaction();
            for (var i = 0; i < previousTransaction.Outputs.Count; i++)
                spendingTransaction.Inputs.Add(new OutPoint(previousTransaction.GetHash(), i));
            spendingTransaction.Outputs.Add(Money.Coins(4.5m), new Key().PubKey.WitHash.ScriptPubKey);

            var psbt = PSBT.FromTransaction(spendingTransaction, network);
            foreach (var input in psbt.Inputs)
                input.NonWitnessUtxo = previousTransaction;
            psbt.Inputs[0].WitnessScript = witnessScript;
            psbt.Inputs[2].RedeemScript = nestedP2wshRedeem;
            psbt.Inputs[2].WitnessScript = witnessScript;
            psbt.Inputs[5].RedeemScript = witnessScript;

            Assert.All(psbt.Inputs, input => Assert.Null(input.WitnessUtxo));

            psbt.AddWitnessUtxoToSegwitInputs();

            for (var i = 0; i < 5; i++)
            {
                Assert.NotNull(psbt.Inputs[i].WitnessUtxo);
                Assert.Equal(previousTransaction.Outputs[i], psbt.Inputs[i].WitnessUtxo);
            }
            Assert.Null(psbt.Inputs[5].WitnessUtxo);
            Assert.All(psbt.Inputs, input => Assert.Same(previousTransaction, input.NonWitnessUtxo));
        }

        [Fact]
        [Trait("Playwright", "Playwright")]
        public async Task CanPlayWithPSBT()
        {
            await using var s = CreatePlaywrightTester(newDb: true);
            await s.StartAsync();

            await s.RegisterNewUser(true);
            var hot = await s.CreateNewStore();
            var seed = await s.GenerateWallet(isHotWallet: true);
            var cold = await s.CreateNewStore();
            await s.GenerateWallet(isHotWallet: false, seed: seed.ToString());

            // Scenario 1: one user has two stores sharing same seed
            // one store is hot wallet, the other not.

            // Here, the cold wallet create a PSBT, then we switch to hot wallet to sign
            // the PSBT and broadcast
            await s.GoToStore(cold.storeId);
            var address = await s.FundStoreWallet();
            await Task.Delay(1000);
            await s.GoToWallet(navPages: Views.Wallets.WalletsNavPages.Send);
            await SendAllTo(s, address);
            await s.Page.ClickAsync("#SignWithPSBT");

            var psbt = await ExtractPSBT(s);

            await s.GoToStore(hot.storeId);
            await s.GoToWallet(s.WalletId, navPages: Views.Wallets.WalletsNavPages.PSBT);
            await s.Page.Locator("[name='PSBT']").FillAsync(psbt);
            await s.Page.ClickAsync("#Decode");
            await s.Page.ClickAsync("#SignTransaction");
            await s.Page.ClickAsync("#BroadcastTransaction");
            await s.FindAlertMessage();

            // Scenario 2: Same as scenario 1, except we create a PSBT from hot wallet, then sign by manually
            // entering the seed on the cold wallet.
            await s.GoToWallet(navPages: Views.Wallets.WalletsNavPages.Send);
            await SendAllTo(s, address);
            psbt = await ExtractPSBT(s);

            // Let's check it has been signed, then remove the signature.
            // Also remove the hdkeys so we can test the update later
            var psbtParsed = PSBT.Parse(psbt, s.Server.NetworkProvider.BTC.NBitcoinNetwork);
            var signedPSBT = psbtParsed.Clone();
            Assert.True(psbtParsed.Clone().TryFinalize(out _));
            Assert.Single(psbtParsed.Inputs[0].PartialSigs);
            psbtParsed.Inputs[0].PartialSigs.Clear();
            Assert.Single(psbtParsed.Inputs[0].HDKeyPaths);
            psbtParsed.Inputs[0].HDKeyPaths.Clear();
            var skeletonPSBT = psbtParsed;

            await s.GoToStore(cold.storeId);
            await s.GoToWallet(s.WalletId, navPages: Views.Wallets.WalletsNavPages.PSBT);
            await s.Page.Locator("[name='PSBT']").FillAsync(skeletonPSBT.ToBase64());
            await s.Page.ClickAsync("#Decode");
            await s.Page.ClickAsync("#SignTransaction");
            await s.Page.ClickAsync("#SignWithSeed");
            await s.Page.Locator("[name='SeedOrKey']").FillAsync(seed.ToString());
            await s.Page.ClickAsync("#Submit");
            await s.Page.ClickAsync("#BroadcastTransaction");
            await s.FindAlertMessage();

            // Let's check if the update feature works
            await s.GoToWallet(navPages: Views.Wallets.WalletsNavPages.PSBT);
            await s.Page.Locator("[name='PSBT']").FillAsync(skeletonPSBT.ToBase64());
            await s.Page.ClickAsync("#Decode");
            await s.Page.ClickAsync("#PSBTOptionsAdvancedHeader");
            await s.Page.ClickAsync("#update-psbt");

            psbt = await ExtractPSBT(s);
            psbtParsed = PSBT.Parse(psbt, s.Server.NetworkProvider.BTC.NBitcoinNetwork);
            Assert.Single(psbtParsed.Inputs[0].HDKeyPaths);
            Assert.Empty(psbtParsed.Inputs[0].PartialSigs);

            // Let's if we can combine the updated psbt (which has hdkeys, but no sig)
            // with the signed psbt (which has sig, but no hdkeys)
            await s.GoToWallet(s.WalletId, navPages: Views.Wallets.WalletsNavPages.PSBT);
            await s.Page.Locator("[name='PSBT']").FillAsync(psbtParsed.ToBase64());
            await s.Page.ClickAsync("#Decode");
            await s.Page.ClickAsync("#PSBTOptionsAdvancedHeader");
            await s.Page.ClickAsync("#combine-psbt");
            signedPSBT.Inputs[0].HDKeyPaths.Clear();
            await s.Page.Locator("[name='PSBT']").FillAsync(signedPSBT.ToBase64());
            await s.Page.ClickAsync("#Submit");
            psbt = await ExtractPSBT(s);
            psbtParsed = PSBT.Parse(psbt, s.Server.NetworkProvider.BTC.NBitcoinNetwork);
            Assert.Single(psbtParsed.Inputs[0].HDKeyPaths);
            Assert.Single(psbtParsed.Inputs[0].PartialSigs);
        }

        [Fact]
        [Trait("Playwright", "Playwright")]
        public async Task CanBroadcastASignedTransaction()
        {
            // The round trip a signer that finalizes on the device forces: what comes
            // back is a transaction, so it has to go in where a PSBT would.
            await using var s = CreatePlaywrightTester(newDb: true);
            await s.StartAsync();

            await s.RegisterNewUser(true);
            await s.CreateNewStore();
            await s.GenerateWallet(isHotWallet: true);
            var address = await s.FundStoreWallet();
            await Task.Delay(1000);

            await s.GoToWallet(navPages: Views.Wallets.WalletsNavPages.Send);
            await SendAllTo(s, address);

            // Signed, and the page offers the transaction itself rather than only the PSBT.
            var transaction = await s.Page.Locator("#transaction-hex").TextContentAsync();
            Assert.NotEmpty(transaction);

            // Nothing has been broadcast yet: do it from the transaction alone.
            await s.GoToWallet(s.WalletId, navPages: Views.Wallets.WalletsNavPages.PSBT);
            await s.Page.Locator("[name='SignedTransaction']").FillAsync(transaction);
            await s.Page.ClickAsync("#DecodeTransaction");
            await s.Page.ClickAsync("#BroadcastTransaction");
            await s.FindAlertMessage();
        }

        private static async Task SendAllTo(PlaywrightTester s, string address)
        {
            await s.Page.Locator("[name='Outputs[0].DestinationAddress']").FillAsync(address);
            await s.Page.ClickAsync(".crypto-balance-link");
            await s.Page.ClickAsync("#SignTransaction");
        }

        private Task<string> ExtractPSBT(PlaywrightTester s) => s.Page.Locator("#psbt-base64").TextContentAsync();
    }
}
