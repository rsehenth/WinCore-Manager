# Recuperação do WinCore Manager / WinCore Manager recovery

## Português (Brasil)

1. Feche o WinCore Manager e aguarde o atualizador terminar. Na pasta portátil original, abra **Recovery\WinCoreManager.Recover.exe** na mesma conta Windows que iniciou a atualização, sem elevar. O assistente funciona mesmo quando WinCoreManager.exe não abre ou está ausente.
2. Consulte a mensagem ao final. O assistente grava o resultado em **Recovery\recovery-*.log** quando consegue abrir o diretório com acesso de gravação. A pasta portátil também precisa permitir acesso de gravação à sua trava **.wincore-portable.lock**. Se houver arquivos em uso, feche os programas indicados e tente novamente. Se faltar espaço, libere espaço fora da pasta portátil. Se o acesso for negado, use a conta original e verifique as permissões da pasta.
3. Após a confirmação de restauração, abra o aplicativo quando quiser. O assistente não inicia o aplicativo. Uma segunda execução verifica o pacote e informa que não há atualização pendente.

### Quando usar uma cópia nova

Siga este procedimento se o assistente estiver ausente, o Windows recusar abri-lo ou o resultado pedir recuperação manual.

1. Preserve a pasta original inteira, incluindo `.wincore-update`, `Recovery`, `settings.json` e `Data`. Mantenha fechadas todas as instâncias que usam esses dados. Preserve também quaisquer arquivos pessoais que estejam fora de `Data`. Evite importar ou executar arquivos de uma transação interrompida.
2. Obtenha um pacote portátil íntegro de uma origem em que você confia. Confira sua assinatura ou compare o SHA-256 com uma referência recebida por um canal independente confiável. Extraia o pacote em uma pasta local **nova**, separada da original.
3. Na nova pasta, copie o `settings.json` legível e a árvore `Data` como um conjunto, incluindo os arquivos SQLite auxiliares `-wal` e `-shm` que existirem. Faça a cópia com as instâncias fechadas. Copie arquivos pessoais separadamente e mantenha os binários da extração nova. Se houver erro de leitura ou falta de espaço, preserve ambas as pastas e reúna o log de recuperação e os detalhes do erro para suporte.
4. Abra apenas o aplicativo na pasta nova. Confirme preferências, histórico e arquivos pessoais. Em uma instalação que usa dados locais da conta Windows, preserve também o diretório de dados da conta. Mantenha a pasta original até concluir a conferência.

## English (United States)

1. Close WinCore Manager and wait for the updater to finish. In the original portable folder, open **Recovery\WinCoreManager.Recover.exe** using the same Windows account that started the update, without elevation. The helper works when WinCoreManager.exe is missing or fails to start.
2. Read the result. The helper records it in **Recovery\recovery-*.log** when it can write to that directory. The portable folder must also allow write access to its **.wincore-portable.lock** file. If files are in use, close the programs using them and retry. If disk space is low, free space outside the portable folder. If access is denied, use the original account and check the directory permissions.
3. After recovery confirms that the previous package was restored, open the app when ready. The helper does not start the app. Running the helper again verifies the package and reports that no update is pending.

### When to use a fresh copy

Follow this procedure if the helper is missing, Windows rejects it, or the result requests manual recovery.

1. Keep the complete original folder, including `.wincore-update`, `Recovery`, `settings.json`, and `Data`. Keep all instances that use those data closed. Preserve any personal files outside `Data` as well. Avoid importing or running files from the interrupted transaction.
2. Obtain an intact portable package from a source you trust. Check its signature or compare its SHA-256 hash to a reference obtained over an independent trusted channel. Extract the package to a **new**, separate local directory.
3. Copy readable `settings.json` and the complete `Data` tree together, including any SQLite `-wal` and `-shm` files. Keep the app closed while copying. Copy personal files separately and retain the newly extracted binaries. If there is a read error or insufficient space, keep both folders and collect the recovery log and error details for support.
4. Open the app from the new directory. Verify preferences, history, and personal files. If the installation uses the Windows account's local data, preserve that directory too. Keep the original folder until these checks are complete.
