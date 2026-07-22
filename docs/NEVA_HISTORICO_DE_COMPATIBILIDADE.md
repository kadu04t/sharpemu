# Histórico de compatibilidade do Neva no SharpEmu

Data do registro: 21 de julho de 2026  
Branch analisada: `Neva`  
Executável do jogo usado nos testes: `D:\Documentos\Coisas para sla mano\JOGOS PIRATAS\Neva\eboot.bin`

## Atualização mais recente — gameplay alcançada

Esta atualização substitui o “ponto atual” descrito originalmente mais abaixo. Em 21 de julho de 2026, o Neva ultrapassou o menu, iniciou `New Game`, entrou na cutscene, aceitou o comando para pulá-la e chegou à gameplay. O usuário confirmou que já era possível controlar e jogar, ainda que com renderização incompleta, desempenho variável e instabilidade entre inicializações.

O avanço final não veio de uma correção no FFmpeg. O vídeo já estava sendo decodificado; o bloqueio seguinte estava no caminho gráfico durante a transição para o nível. A combinação que chegou à gameplay foi:

- correção das proteções de memória através de reservas Win32 adjacentes;
- escrita paginada de grandes leituras `pread` em regiões guest contíguas;
- fallback experimental para o pipeline gráfico de chave `D12C52CE`, que bloqueava o driver AMD durante sua criação;
- manutenção do cache Vulkan normal e instrumentação pesada desativada durante o boot;
- ativação tardia, por arquivo-gatilho, dos diagnósticos da fila Vulkan somente na cutscene.

O rastreamento pós-cutscene mostrou que `vkEndCommandBuffer`, `vkQueueSubmit` e os flushes continuavam concluindo normalmente. Portanto, a hipótese de bloqueio da fila Vulkan foi descartada. O fallback `D12C52CE` foi acionado uma vez e permitiu que a execução avançasse até a gameplay. Ele continua sendo um experimento de causalidade, não uma implementação gráfica definitiva.

## Objetivo e escopo

Este documento reconstrói o trabalho feito desde a primeira tentativa de iniciar o Neva até o estado atual. Ele reúne:

- os sintomas observados em cada etapa;
- a causa encontrada ou a hipótese confirmada pelos testes;
- as alterações feitas no código e o motivo de cada uma;
- os arquivos de produção e de teste envolvidos;
- os experimentos temporários que não devem ser tratados como solução definitiva;
- os resultados obtidos e os problemas que ainda estão abertos.

O inventário foi conferido contra o commit `8953801` (`Neva tests`), o commit `b504435` e o estado atual ainda não commitado da árvore de trabalho. Artefatos de diagnóstico são listados separadamente, pois não fazem parte do código que será distribuído.

## Resumo do avanço

O jogo passou pelos seguintes estados:

1. tela preta, primeiro frame travado e crashes ocasionais;
2. splash exibida por aproximadamente um segundo, seguida de congelamento;
3. avanço do segundo frame e estabilização parcial do loop gráfico;
4. carregamento do Unity e chegada ao menu principal;
5. seleção de `New Game` e da dificuldade;
6. entrada no carregamento da fase/cutscene anterior ao gameplay;
7. abertura do vídeo embutido em `resources.resource`, inicialização do FFmpeg e entrega dos primeiros frames decodificados ao guest;
8. correção de falhas de `mprotect` e `pread` que corrompiam o carregamento de assets;
9. identificação e substituição experimental do pipeline Vulkan `D12C52CE`, que bloqueava o driver AMD;
10. pulo da cutscene e chegada confirmada à gameplay controlável.

O ponto atual é: a cutscene ainda não é apresentada com fidelidade, mas já pode ser pulada e a execução alcança gameplay controlável. O objetivo seguinte deixou de ser “chegar ao jogo” e passou a ser corrigir o pipeline `D12C52CE`, a composição NV12, a estabilidade do boot e o desempenho durante a gameplay.

## Linha do tempo técnica

### 1. Import AGC não resolvido e framebuffer preto

No início, o import `sceAgcDriverGetEqContextId`, NID `Zw7uUVPulbw`, não estava resolvido. A análise do retorno em `0x8014ED3CF` mostrou que o caller consumia `EAX` diretamente como identificador de contexto, e não apenas como um código de sucesso/erro.

Foi criado primeiro um experimento controlado por `SHARPEMU_EXPERIMENT_AGC_EQ_CONTEXT_ID`. Ele permitiu testar a causalidade sem escrever nos ponteiros do jogo e sem apresentar um valor inventado como implementação final. Também foram adicionados logs dos argumentos do import e do símbolo bruto preservado pelo loader.

Depois, o experimento foi substituído por uma implementação baseada no evento real:

- eventos gráficos usam o campo `data` do `kevent` como ID do contexto;
- eventos de outro filtro usam `ident` como fallback;
- eventos AGC de conclusão deixaram de ser indevidamente coalescidos, preservando cada ID de contexto submetido.

Isso retirou o import do caminho não resolvido e ligou a resposta ao estado real da fila gráfica.

### 2. Diferença entre flip submetido e flip concluído

O jogo conseguia produzir uma imagem inicial, mas o segundo frame não avançava de forma correta. O `sceVideoOutGetFlipStatus` não informava o `flipArg` da última apresentação concluída. Para o Neva, essa informação é usada pela thread de flip para saber quando um buffer pode ser reciclado.

O estado de VideoOut passou a manter `LastCompletedFlipArg`. Esse valor só é atualizado dentro da ação executada depois que o trabalho gráfico anterior termina, e é escrito no offset `0x18` de `SceVideoOutFlipStatus`.

Resultado: o estado passou a distinguir uma submissão apenas enfileirada de uma apresentação realmente concluída. Isso foi uma das peças necessárias para ultrapassar o primeiro frame.

### 3. Espera e despertar por endereço

O Unity usa sincronização por endereço para coordenar threads de carregamento e renderização. A implementação foi ajustada para:

- bloquear cooperativamente uma guest thread no scheduler;
- associar a espera a uma chave derivada do endereço;
- incrementar uma geração antes do wake, evitando a janela de lost wakeup;
- despertar as threads bloqueadas pelo scheduler;
- manter um fallback de espera no host para callers não cooperativos;
- permitir diagnóstico amostrado com `SHARPEMU_LOG_SYNC_ON_ADDRESS=1`.

Esse trabalho evitou retornos prematuros e esperas perdidas durante o carregamento. Os logs `[SYNCDIAG]` ainda estão marcados no código como diagnóstico temporário.

### 4. Suspensão de threads pelo GC do Unity

O coletor do Unity/IL2CPP usa exceções/sinais guest para suspender threads durante ciclos stop-the-world. Uma thread estacionada dentro de um import HLE ou esperando um mutex não alcançava um ponto seguro, então o GC ficava esperando uma confirmação que nunca chegava.

As mudanças realizadas foram:

- capturar a continuação do import atual;
- permitir que imports HLE bloqueantes atendam uma exceção guest pendente;
- fazer a espera de mutex do host consultar esse ponto seguro em intervalos curtos;
- entregar imediatamente para executores persistentes quando o alvo permite;
- preservar corretamente threads que já estavam no estado `Ready`;
- enfileirar um novo ciclo de suspensão que chega enquanto o handler anterior ainda está retornando;
- entregar no máximo uma exceção pendente por safe point.

O último item foi decisivo. O Unity retransmite o sinal de suspensão até receber a confirmação. Drenar a fila em um `while` fazia o slot ser reabastecido continuamente, impedindo a thread de retornar ao código guest e escrever o acknowledgement. Agora uma repetição coalescida fica para o próximo limite de import.

Também foi ampliada de 64 para 1024 a faixa de slots de stack guest reconhecida por `pthread_attr_get_np`. Sem isso, o Boehm GC podia deixar de encontrar roots em threads alocadas nos slots mais altos.

Resultado: o carregamento avançou por `resources.assets`, menu e níveis seguintes, embora ainda exista alguma intermitência de suspensão na inicialização.

### 5. Corrupção `Rewired_` e caminho de memória libc

Um crash separado apresentava:

- alvo da violação: `0xFFFFFFFFFFFFFFFF`;
- `R15 = 0x5F64657269776552`, os bytes ASCII de `Rewired_`;
- instrução aproximada: `mov rdx, [r15]`.

Foram adicionados um watchpoint nativo opcional e um recuperador estritamente experimental para observar a escrita que introduzia esse marcador numa free list. O recuperador é protegido por `SHARPEMU_EXPERIMENT_RECOVER_CORRUPT_GUEST_FREE_LIST=1` e não é uma correção apropriada para produção.

O teste mais útil foi separar apenas `memcpy`/`memmove`/`memset` para HLE, mantendo a família de allocators do LLE. Isso é habilitado por `SHARPEMU_EXPERIMENT_HLE_LIBC_MEMORY=1`. O objetivo era isolar corrupção no caminho de cópia/preenchimento sem alterar ownership ou layout das alocações.

Com esse caminho, não foi necessário manter o recuperador da free list habilitado para chegar ao menu. O crash `Rewired_` deve continuar tratado como um problema independente caso reapareça.

### 6. Vazamento de objetos opacos pthread

Depois do avanço do GC, o jogo executava uma quantidade enorme de inicializações e destruições de mutexes e atributos. O destroy removia apenas um alias do dicionário, não liberava a alocação guest usada como handle opaco e deixava aliases internos vivos. O efeito acumulado era exaustão/corrupção de memória e falhas em novas inicializações.

A correção passou a:

- guardar o `OpaqueHandle` nos estados de mutex e condition variable;
- remover todos os aliases que apontam para o mesmo estado;
- liberar o objeto opaco pelo `IGuestMemoryAllocator` no destroy;
- zerar o ponteiro armazenado no objeto guest;
- liberar a alocação também em todos os caminhos de falha de inicialização;
- aplicar a mesma disciplina a mutex attributes e condition variables.

Resultado: desapareceu o crescimento causado por handles opacos abandonados e o menu ficou muito mais estável.

### 7. ABI e rotinas auxiliares do runtime

Outras incompatibilidades encontradas durante o carregamento foram corrigidas:

- `sceSysmoduleGetModuleInfoForUnwind` passou a reutilizar a implementação ABI-compatível de `sceKernelGetModuleInfoForUnwind`, permitindo que o unwinder encontre EH frames de PRX carregados;
- conversões UTC/local passaram a escrever o layout completo de 16 bytes de `OrbisTimesec`, com `west_sec` e `dst_sec` nos offsets corretos;
- valores de tempo fora da faixa retornam argumento inválido em vez de gerar exceção host;
- `PosixUnlink` foi adicionado ao caminho de compatibilidade;
- release de memória direta ganhou rastreamento para mostrar o mapa de alocações durante a investigação.

O erro no layout de tempo fazia a libc ler campos não inicializados e podia fazê-la procurar transições de timezone por uma faixa enorme.

### 8. Queda de desempenho aparente

As execuções a aproximadamente 0,5 FPS ocorreram principalmente com rastreamento AGC/guest muito pesado. Uma execução limpa do menu voltou a aproximadamente 25–28 flips/FPS. Portanto, aquela queda específica não foi causada pelo AvPlayer, que ainda nem estava reproduzindo a cutscene nessa etapa.

### 9. `New Game` e vídeo embutido em `resources.resource`

Ao iniciar um jogo, o AvPlayer recebia:

```text
[AVPLAYER][INFO] file_open guest='/app0/Media/resources.resource'
[AVPLAYER][INFO] file_size_inferred format=iso-bmff
[AVPLAYER][ERROR] Could not open guest video
```

O caminho não apontava para um MP4 independente. O arquivo é um container de recursos do Unity e contém um segmento ISO-BMFF embutido no offset lógico `0x62647817`, com tamanho `544.593.183` bytes.

O AvPlayer passou a usar os callbacks de arquivo fornecidos pelo jogo:

- open;
- close;
- leitura com offset;
- consulta de tamanho.

Como o callback de tamanho retornava zero, foi implementado um fallback controlado:

1. ler os primeiros 64 bytes pelo callback guest;
2. confirmar o prefixo `ftyp`;
3. obter o offset lógico do objeto de arquivo guest em `+0x118`;
4. percorrer as boxes ISO-BMFF no arquivo host até encontrar `mdat` e `moov`;
5. confirmar que o prefixo do segmento host corresponde ao lido pelo callback;
6. materializar somente o segmento de vídeo em um arquivo temporário, em blocos de 1 MiB.

Para suportar o callback de leitura corretamente, o scheduler passou a aceitar quatro argumentos guest. O quarto argumento é colocado em `RCX`, resultando na chamada `(objeto, buffer, offset, tamanho)` esperada pelo jogo.

O arquivo materializado foi validado como H.264, 2560×1440, 60 FPS, duração aproximada de 216,47 segundos e sem faixa de áudio.

### 10. FFmpeg, stream de áudio inexistente e buffers de textura

O FFmpeg/ffprobe disponível na instalação do Krita não estava no `PATH`. O AvPlayer agora procura:

- `SHARPEMU_FFMPEG_PATH`;
- a pasta do executável;
- `tools` ao lado do executável;
- pastas conhecidas do Krita em `Program Files`.

O player também deixou de anunciar sempre dois streams. O ffprobe verifica se existe áudio; para esse vídeo, o stream count é um e pedidos de informações/frames de áudio são rejeitados normalmente. Isso evitou a falha `ffmpeg -map 0:a:0` em um arquivo que contém apenas vídeo.

O callback guest de alocação de textura executava com sucesso `sceKernelAllocateDirectMemory` e `sceKernelMapDirectMemory`, mas retornava zero ao host. A disassembly do callback em `0x801502280` confirmou a ABI `(objeto, alinhamento, tamanho)` e mostrou que o mapeamento realmente concluía.

Foi acrescentado um fallback estreito:

- o kernel registra, por thread host, o último mapeamento direto bem-sucedido;
- antes do callback, esse registro é zerado;
- somente se o callback terminar normalmente, retornar zero e tiver produzido um mapeamento na mesma thread, o AvPlayer recupera aquele endereço.

Foram recuperados buffers como `0x19300000`, `0x19850000` e `0x19DA0000`. Os primeiros frames foram entregues com timestamps 0, 750, 967, 1033, 1050 e 1067 ms.

Esse fallback ainda é uma compatibilidade direcionada. A causa raiz da perda do valor de retorno em uma chamada guest aninhada deve ser investigada antes de generalizar o comportamento.

### 11. Proteção de memória através de reservas Win32 adjacentes

O `sceKernelMprotect` podia receber uma faixa guest logicamente contínua que, no host, atravessava mais de uma reserva Win32. Uma única chamada a `VirtualProtect` não pode atravessar limites de `AllocationBase`, mesmo quando todas as páginas estão committed e são adjacentes.

`TryProtectHostRange` passou a consultar previamente toda a faixa com `VirtualQuery` e aplicar a proteção separadamente em cada região host. Depois da mudança, as falhas de `mprotect` desapareceram nas execuções do Neva.

### 12. Grandes leituras `pread` cruzando regiões guest

As leituras grandes de assets também falhavam quando o destino atravessava duas regiões internas adjacentes. `PhysicalVirtualMemory.TryWrite` exige que uma escrita pertença a uma única região, embora o buffer seja válido do ponto de vista do guest.

`KernelFileExtendedExports` ganhou `TryWriteGuestBufferPaged`, que divide a cópia em páginas guest de `0x4000` bytes. O novo teste cobre tanto duas regiões adjacentes válidas quanto um buraco real, que continua retornando erro. Depois disso:

- deixaram de ocorrer erros `pread MEMORY_FAULT`;
- a reserva envenenada em `0xFFFFFFF6AAAC0000` não reapareceu;
- o crash antigo do `Loading.PreloadManager` com ponteiro `0xAAAAAAAC` deixou de ocorrer nesse caminho.

### 13. Pipelines Vulkan que bloqueavam ou derrubavam o driver AMD

Durante a transição pós-cutscene, o driver AMD bloqueava na criação de um pipeline gráfico. Somente o hash do pixel shader não era suficiente, pois variava entre execuções e também aparecia em pipelines válidos. Foi criada uma chave estrutural completa, incluindo shaders, formatos, depth, topologia, recursos, vértices e estado de rasterização.

A chave exata problemática foi `D12C52CE`, com:

- vertex shader `9F7F5B3C`;
- pixel shader `FF4A1CC3`;
- formato de cor `37` e depth habilitado;
- topologia `TriangleList`;
- cull front desabilitado, cull back habilitado e front face clockwise;
- depth test/write habilitados, compare op `6`.

Ignorar completamente essa chave evitava o crash, mas o jogo reenviava o mesmo passe indefinidamente. Por isso foi adicionado `SHARPEMU_EXPERIMENT_FALLBACK_GRAPHICS_PIPELINE_KEYS`: para a chave selecionada, o presenter cria temporariamente um pipeline fullscreen simples com fragmento sólido. Com `D12C52CE`, o fallback foi acionado e a transição chegou à gameplay.

Também foi observado um crash intermitente diferente dentro de `vkCreateComputePipelines` durante o boot. Ele continua aberto e explica parte das execuções que morrem antes do menu.

### 14. Diagnóstico tardio da fila e confirmação da gameplay

Ativar logs de fila desde o início alterava o timing e aumentava a frequência de travamentos antes do menu. `VulkanVideoPresenter` passou a aceitar `SHARPEMU_LOG_VK_QUEUE_FLOW_TRIGGER_FILE`; um timer verifica o arquivo e liga o rastreamento somente quando ele aparece.

Com o gatilho criado já na cutscene, foram registrados repetidamente:

- `end_command_begin` seguido de `end_command_done`;
- `submit_begin` seguido de `submit_done`;
- `flush_begin` seguido de `flush_done`.

O processo permaneceu ativo, consumindo trabalho AGC, e o usuário confirmou depois do pulo que já estava na gameplay e conseguia jogar. Isso encerrou o objetivo inicial de chegar ao jogo propriamente dito e deslocou o trabalho seguinte para fidelidade visual, desempenho e estabilidade.

Foi acrescentada ainda uma sonda `agc.flip_flow`, protegida pelo mesmo gatilho, para registrar por que um pacote `RFlip` eventualmente não vira uma apresentação: buffer desconhecido, imagem Vulkan não registrada ou ausência de fallback de desenho. Ela é diagnóstico e deve ficar desligada no boot.

## Inventário dos arquivos alterados

### Alterações já registradas no commit `8953801`

| Arquivo | Alteração principal | Motivo |
|---|---|---|
| `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Exceptions.cs` | Watchpoint nativo opcional, diagnóstico da função do crash e recuperador experimental da free list com `Rewired_`. | Encontrar a origem da corrupção sem mascará-la como solução final. |
| `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Imports.cs` | Logs de import não resolvido, experimento do ID EQ e captura de argumentos do NID `Zw7uUVPulbw`. | Determinar a assinatura e testar causalidade do import AGC. |
| `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.cs` | Caminho seletivo HLE para memória libc, safe point em imports bloqueantes e ajustes na entrega de exceções guest. | Evitar corrupção no bulk memory e permitir o stop-the-world do Unity. |
| `src/SharpEmu.Core/Loader/SelfLoader.cs` | Log opcional do símbolo bruto do import EQ. | Recuperar biblioteca/módulo e evitar adivinhar a assinatura pelo NID isolado. |
| `src/SharpEmu.HLE/GuestThreadExecution.cs` | Interface para atender exceções em imports bloqueantes e captura da continuação atual. | Dar ao scheduler um ponto seguro para suspender threads presas em HLE. |
| `src/SharpEmu.Libs/Agc/AgcExports.cs` | Implementação inicial de `sceAgcDriverGetEqContextId` e rastreamento dos IDs de conclusão. | Substituir o fallback não resolvido por estado gráfico real. |
| `src/SharpEmu.Libs/Kernel/KernelEventQueueCompatExports.cs` | Disparo não coalescido de eventos por filtro. | Preservar um evento e um context ID para cada conclusão AGC. |
| `src/SharpEmu.Libs/Kernel/KernelMemoryCompatExports.cs` | `PosixUnlink` e diagnóstico detalhado de liberação de memória direta. | Completar imports usados pelo título e investigar ownership/alocações. |
| `src/SharpEmu.Libs/Kernel/KernelPthreadCompatExports.cs` | Espera de mutex host com polling de safe point e controles de diagnóstico. | Permitir sinais do GC enquanto uma guest thread aguarda mutex. |
| `src/SharpEmu.Libs/Kernel/KernelPthreadExtendedCompatExports.cs` | Reconhecimento de 1024 slots de stack do executor. | Fazer o GC incluir roots de todas as threads guest, não apenas dos 64 primeiros slots. |
| `src/SharpEmu.Libs/Kernel/KernelRuntimeCompatExports.cs` | Alias de unwind de sysmodule e correção do ABI das conversões de tempo. | Corrigir unwind de PRX e impedir loops/falhas causados por `OrbisTimesec` incorreto. |
| `tests/SharpEmu.Libs.Tests/Agc/AgcEventQueueTests.cs` | Testes de eventos AGC não coalescidos e retorno do context ID. | Proteger a semântica descoberta. |
| `tests/SharpEmu.Libs.Tests/Kernel/KernelRuntimeCompatExportsTests.cs` | Round-trip UTC/local e validação do layout completo. | Evitar regressão do ABI de tempo. |
| `tests/SharpEmu.Libs.Tests/Pthread/PthreadAttributeSemanticsTests.cs` | Teste dos slots de stack do executor. | Garantir que threads em slots altos sejam reconhecidas. |
| `tests/SharpEmu.Libs.Tests/Pthread/PthreadMutexSemanticsTests.cs` | Teste de entrega de exceção durante mutex bloqueado e scheduler fake. | Validar o safe point usado pelo GC. |

### Ajuste de organização no commit `b504435`

| Arquivo | Alteração | Motivo |
|---|---|---|
| `.gitignore` | Inclusão de `nids.txt`. | Não versionar o arquivo local gerado durante a análise de imports/NIDs. |

### Alterações atuais ainda não commitadas

| Arquivo | Alteração principal | Motivo |
|---|---|---|
| `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.cs` | Overload de callback com quatro argumentos; coalescência e entrega de apenas uma exceção guest por safe point. | Suportar o callback de leitura do AvPlayer e impedir starvation no acknowledgement do GC. |
| `src/SharpEmu.HLE/GuestThreadExecution.cs` | Novo contrato de `TryCallGuestFunction` com `arg3`. | Expor ao HLE a chamada guest de quatro argumentos. |
| `src/SharpEmu.Libs/Agc/AgcExports.cs` | EQ context usa `data` apenas para filtro gráfico e `ident` nos demais. | Reproduzir a semântica do tipo de evento em vez de assumir que todo `data` é um ID gráfico. |
| `src/SharpEmu.Libs/AvPlayer/AvPlayerExports.cs` | Callbacks de arquivo, extração ISO-BMFF, fonte temporária, detecção de áudio, localização do FFmpeg e recuperação estreita de textura mapeada. | Abrir e decodificar a cutscene embutida no container Unity. |
| `src/SharpEmu.Libs/Kernel/KernelMemoryCompatExports.cs` | Registro thread-static do último mapeamento direto bem-sucedido. | Recuperar o endereço apenas no caso específico em que o callback de textura retorna zero após mapear. |
| `src/SharpEmu.Libs/Kernel/KernelFileExtendedExports.cs` | Escrita paginada de buffers de `pread` que atravessam regiões guest adjacentes. | Evitar `MEMORY_FAULT` em leituras grandes e impedir corrupção no carregamento de assets. |
| `src/SharpEmu.Libs/Kernel/KernelPthreadCompatExports.cs` | Ownership explícito de handles opacos, remoção de aliases e free em destroy/falhas. | Eliminar o vazamento que exauria memória durante a criação repetida de mutexes/atributos/conds. |
| `src/SharpEmu.Libs/Kernel/KernelSyncOnAddressCompatExports.cs` | Espera cooperativa, geração anti-lost-wakeup, wake pelo scheduler e logs opcionais. | Fazer a sincronização do Unity progredir entre frames e durante o loading. |
| `src/SharpEmu.Libs/VideoOut/VideoOutExports.cs` | Armazenamento e exposição do `LastCompletedFlipArg`. | Permitir que o jogo recicle buffers somente depois do flip concluído. |
| `src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs` | Chave completa de pipeline, filtros/fallbacks experimentais, marcadores de criação e rastreamento tardio da fila Vulkan. | Isolar os pipelines que bloqueiam o driver AMD e testar a transição até a gameplay sem perturbar o boot. |
| `tests/SharpEmu.Libs.Tests/Agc/AgcEventQueueTests.cs` | Filtro gráfico explícito e fallback para `ident`. | Cobrir a semântica refinada do EQ context. |
| `tests/SharpEmu.Libs.Tests/AvPlayer/AvPlayerStreamInfoTests.cs` | Player sem áudio anuncia um stream e rejeita stream info de áudio. | Impedir que vídeos video-only inicializem um decoder de áudio inexistente. |
| `tests/SharpEmu.Libs.Tests/Pthread/PthreadMutexSemanticsTests.cs` | Verificação de que mutex e attribute opacos são liberados. | Evitar a volta do vazamento observado no menu. |
| `tests/SharpEmu.Libs.Tests/AvPlayer/AvPlayerIsoBmffTests.cs` | Novo teste do cálculo do segmento até `mdat` + `moov` e rejeição de container incompleto. | Validar a extração delimitada do MP4 embutido. |
| `tests/SharpEmu.Libs.Tests/Kernel/KernelFileExtendedExportsTests.cs` | Escrita em regiões adjacentes e rejeição de um buraco real. | Proteger a correção de `pread` paginado. |
| `tests/SharpEmu.Libs.Tests/VideoOut/VideoOutFlipStatusTests.cs` | Novo teste do `flipArg` completo, inclusive com bit de sinal. | Proteger o campo de 64 bits devolvido no status do flip. |

Além do uso do EQ context, `AgcExports.cs` contém agora a sonda tardia `agc.flip_flow`, e `KernelMemoryCompatExports.cs` também contém a aplicação segmentada de proteção por região Win32. Essas duas atualizações são posteriores à descrição original das respectivas linhas da tabela.

## Variáveis de ambiente usadas

| Variável | Papel | Situação atual |
|---|---|---|
| `SHARPEMU_EXPERIMENT_HLE_LIBC_MEMORY=1` | Usa HLE apenas para operações de cópia/preenchimento de memória. | Foi o caminho funcional usado para chegar ao menu; ainda é experimental. |
| `SHARPEMU_EXPERIMENT_AGC_EQ_CONTEXT_ID` | Forçava temporariamente um ID EQ. | Não deve ser necessário com o export real; não usar como correção definitiva. |
| `SHARPEMU_EXPERIMENT_RECOVER_CORRUPT_GUEST_FREE_LIST=1` | Ignora de forma muito específica a free list com `Rewired_`. | Diagnóstico somente; deve permanecer desligado em testes normais. |
| `SHARPEMU_LOG_AGC_EQ_CONTEXT=1` | Detalha chamadas e símbolo do import EQ. | Diagnóstico. |
| `SHARPEMU_LOG_GUEST_EXCEPTIONS=1` | Registra entrega de exceções/suspensões guest. | Diagnóstico; gera volume considerável. |
| `SHARPEMU_LOG_SYNC_ON_ADDRESS=1` | Registra waits e wakes amostrados. | Diagnóstico temporário. |
| `SHARPEMU_LOG_PTHREAD_TRYLOCKS=1` | Registra trylocks pthread. | Diagnóstico. |
| `SHARPEMU_LOG_KERNEL_TIME_CONVERSION=1` | Registra as primeiras conversões de tempo. | Diagnóstico. |
| `SHARPEMU_NATIVE_WRITE_WATCH`, `SHARPEMU_NATIVE_WRITE_WATCH_VALUE`, `SHARPEMU_NATIVE_WRITE_WATCH_THREAD` | Configuram o watchpoint nativo da corrupção. | Investigação específica do crash `Rewired_`. |
| `SHARPEMU_FFMPEG_PATH` | Define um FFmpeg/ffprobe alternativo. | Opcional; há descoberta automática no Windows. |
| `SHARPEMU_AVPLAYER_CACHE_DIR` | Define onde fontes guest materializadas são armazenadas temporariamente. | Opcional. |
| `SHARPEMU_VK_PIPELINE_CACHE=0` | Desliga cache de pipeline para comparação. | Diagnóstico apenas; não confirmou ser a causa do crash AMD. |
| `SHARPEMU_LOG_VK_PIPELINE_CREATE=1` | Registra o pipeline imediatamente antes de sua criação e a chave completa em cache miss. | Diagnóstico; deve ficar desligado no boot normal. |
| `SHARPEMU_EXPERIMENT_SKIP_UNATTRIBUTED_DEPTH_GRAPHICS=1` | Descarta experimentalmente draws de depth sem atributos. | Experimento usado nas execuções que avançaram; ainda precisa de validação isolada. |
| `SHARPEMU_EXPERIMENT_SKIP_GRAPHICS_PIPELINE_KEYS` | Ignora chaves estruturais de pipeline selecionadas. | Demonstrou causalidade, mas não permite progresso quando o jogo reenvia o passe. |
| `SHARPEMU_EXPERIMENT_FALLBACK_GRAPHICS_PIPELINE_KEYS=D12C52CE` | Substitui a chave problemática por um pipeline fullscreen sólido. | Experimento que permitiu chegar à gameplay; não deve ser enviado como correção final. |
| `SHARPEMU_LOG_VK_QUEUE_FLOW=1` | Registra end command buffer, submit, fence e flush. | Muito intrusivo quando ligado desde o boot. |
| `SHARPEMU_LOG_VK_QUEUE_FLOW_TRIGGER_FILE` | Ativa o rastreamento de fila quando o arquivo indicado aparece. | Forma preferida de diagnosticar somente a cutscene/gameplay. |
| `SHARPEMU_LOG_AGC_FLIP_FLOW=1` | Registra a rota de cada pacote `RFlip`. | Diagnóstico direcionado; também pode ser ativado pelo arquivo-gatilho da fila. |

## Validações e evidências

Durante a investigação foram registrados:

- build Release completo sem erros; permaneceu apenas o warning preexistente `CA2014` em `Ngs2Exports.cs`;
- suíte `SharpEmu.Libs.Tests` aprovada com 510/510 testes;
- testes focados de pthread passando, incluindo liberação dos objetos opacos;
- testes do AvPlayer passando, incluindo vídeo sem áudio e parser ISO-BMFF;
- chegada repetida ao menu e seleção de `New Game`;
- extração válida do vídeo de 544.593.183 bytes;
- `decoder_started` e entrega dos primeiros frames aos buffers guest;
- uma execução limpa do menu na faixa de aproximadamente 25–28 FPS/flips;
- fallback `D12C52CE` acionado sem fatal no pipeline gráfico;
- milhares de ciclos Vulkan com `end_command`, `submit` e `flush` concluídos depois do pulo da cutscene;
- chegada confirmada à gameplay, com controle do personagem e possibilidade de jogar.

Artefatos principais em `artifacts/diagnostics`:

| Artefato | Conteúdo |
|---|---|
| `Neva-clean-after-gc-20260721-184922.stderr.log` | Execução limpa que chegou ao menu após os ajustes de GC. |
| `Neva-newgame-black-20260721-185551.stderr.log` | Execução salva depois de `New Game` e escolha da dificuldade, ainda em tela preta. |
| `Neva-pthread-release-20260721-183143.stderr.log` | Validação do conserto de liberação pthread. |
| `Neva-gc-coalesce-20260721-184722.stderr.log` | Diagnóstico da coalescência/entrega das suspensões do GC. |
| `Neva-nopipelinecache-20260721-183820.stderr.log` | Comparação com o cache Vulkan desabilitado. |
| `Neva-avplayer-directmem-20260721-191447.stderr.log` | Investigação da alocação direta usada pelo callback de textura. |
| `Neva-cutscene-mappedtexture-retry-20260721-192307.stderr.log` | Execução em que os frames decodificados foram enviados aos buffers mapeados. |
| `Neva-cutscene-livecapture-20260721-192449.stderr.log` | Captura mais recente do comportamento da cutscene. |
| `Neva-embedded-cutscene.mp4` | Segmento de vídeo extraído para validação, com 544.593.183 bytes. |

Logs locais posteriores, mantidos na raiz do workspace durante a investigação:

| Artefato | Conteúdo |
|---|---|
| `neva_pipeline_key_probe_20260721_230438.stderr.log` | Bloqueio que permitiu identificar a chave completa `D12C52CE`. |
| `neva_skip_pipeline_D12C52CE_20260721_230724.stderr.log` | 1.128 interceptações ao ignorar a chave, sem crash mas também sem progresso. |
| `neva_fallback_pipeline_D12C52CE_20260721_231033.stderr.log` | Primeira ativação do pipeline fullscreen substituto. |
| `neva_queue_trigger_quiet_20260721_232327.stderr.log` | Captura tardia da fila após a cutscene; demonstrou submits/flushes concluindo e precedeu a confirmação da gameplay. |

Também foram usados, somente como ferramentas locais de investigação:

- `artifacts/reference/shadPS4`, para comparação de comportamento;
- `artifacts/reference/DisasmGuest`, para ler/disassemblar callbacks guest em execução.

## Problemas ainda abertos

### A. A cutscene e a gameplay ainda não têm renderização fiel

O AvPlayer entrega frames e o jogo permite pular a cena. A execução já alcançou a gameplay, mas a cutscene apresentou preto, ruído/triângulos corrompidos e composição incompleta em diferentes testes. O `Broken pipe` depois do pause/fechamento continua sendo consequência do encerramento do pipe do FFmpeg, não a causa inicial do defeito visual.

Próximas verificações recomendadas:

1. substituir o fallback sólido de `D12C52CE` pela tradução correta do pipeline original;
2. confirmar a composição NV12 e o vínculo real entre o buffer do AvPlayer e a textura amostrada pelo Unity;
3. usar `agc.flip_flow` para explicar frames que não chegam ao VideoOut;
4. validar formato, pitch, tamanho e sincronização das texturas da cutscene;
5. verificar se o fallback do endereço mapeado está escondendo um bug de retorno em callback aninhado.

### B. Inicialização ainda intermitente

Algumas execuções chegam ao menu/level 3; outras podem parar durante `resources.assets` ou em pontos de suspensão do GC. A mudança de uma exceção por safe point resolveu o starvation principal, mas a coordenação ainda merece stress test e instrumentação de baixo volume.

### C. Crash intermitente no driver Vulkan AMD

Foi observado um fatal durante `vkCreateComputePipelines` no boot e bloqueios durante `vkCreateGraphicsPipelines`. Desabilitar o cache de pipeline não corrigiu o problema. A chave gráfica `D12C52CE` foi isolada e contornada experimentalmente, mas a tradução correta e o crash compute intermitente ainda estão abertos.

### D. Crash antigo do `Loading.PreloadManager`

A dereferência de `Rewired_` como ponteiro ocorreu em outra thread e deve permanecer separada do problema gráfico/AvPlayer. Ela não reapareceu depois das correções de proteção e `pread`, mas ainda não há prova suficiente para declarar sua causa definitivamente resolvida. O recuperador experimental da free list não deve entrar numa PR.

## Estado de cada correção

| Área | Estado |
|---|---|
| Resolução real de `sceAgcDriverGetEqContextId` | Implementada e coberta por testes. |
| Eventos AGC sem coalescer IDs distintos | Implementado e coberto por testes. |
| `flipArg` concluído no VideoOut | Implementado e coberto por teste novo. |
| Safe point do GC em imports/mutex | Implementado; melhorou muito, ainda requer stress test. |
| Reconhecimento de stacks pthread | Implementado e coberto por teste. |
| Vazamento de handles opacos pthread | Corrigido e coberto por teste. |
| ABI de tempo e unwind de PRX | Corrigido e coberto parcialmente por testes. |
| Arquivo de cutscene embutido | Encontrado, delimitado e materializado. |
| Detecção de vídeo sem áudio | Implementada e coberta por teste. |
| Callback guest com quatro argumentos | Implementado. |
| Retorno zero do callback de textura | Contornado de forma estreita; causa raiz aberta. |
| `mprotect` através de reservas Win32 adjacentes | Corrigido por região host; validado em execução. |
| `pread` através de regiões guest adjacentes | Corrigido e coberto por testes. |
| Apresentação completa da cutscene | Skippable e suficiente para chegar à gameplay; fidelidade visual ainda aberta. |
| Pipeline gráfico `D12C52CE` | Causa de bloqueio confirmada; fallback experimental chega à gameplay. |
| Fila Vulkan após a cutscene | Confirmada saudável; hipótese de bloqueio descartada. |
| Gameplay | Alcançada e controlável; renderização, desempenho e estabilidade ainda incompletos. |
| Crash Vulkan AMD | Parcialmente isolado; compute crash continua aberto e intermitente. |
| Corrupção `Rewired_` | Não reapareceu após as correções de memória/leitura; causalidade definitiva aberta. |

## Observação para futura PR

Antes de transformar este trabalho em uma PR limpa, os itens puramente diagnósticos devem ser revisados ou removidos, principalmente:

- override `SHARPEMU_EXPERIMENT_AGC_EQ_CONTEXT_ID`;
- recuperação `SHARPEMU_EXPERIMENT_RECOVER_CORRUPT_GUEST_FREE_LIST`;
- watchpoint nativo e dumps específicos de `Rewired_`;
- logs temporários `[SYNCDIAG]`;
- fallback de último direct mapping, caso a causa raiz do retorno do callback seja corrigida;
- filtros `SHARPEMU_EXPERIMENT_SKIP_*` usados para isolar passes gráficos;
- substituição fullscreen da chave `D12C52CE`;
- marcadores de criação de pipeline, fila Vulkan e `agc.flip_flow`, mantendo apenas instrumentação de baixo custo que seja útil genericamente.

As correções com semântica confirmada e testes — eventos AGC, EQ context, flip status, ABI de tempo, slots de stack, ownership pthread, parser ISO-BMFF e vídeo sem áudio — são as partes mais maduras do conjunto.
