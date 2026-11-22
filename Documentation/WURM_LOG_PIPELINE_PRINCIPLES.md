# Wurm — zasady wspólnego przetwarzania logów

Ten dokument określa uzgodnione zasady docelowe dla WurmMiningCounter (WMC), WurmMessageNotifier (WMN) i WurmSkillTracker (WST). Nie oznacza, że wszystkie opisane zachowania są już zaimplementowane w każdym projekcie. Aktualny stan i kontrakty pozostają opisane w dokumentacji danego projektu.

Docelowy przepływ to **Discovery → Monitoring → Dystrybucja → Moduły funkcjonalne**. Discovery rozpoznaje lokalizacje i źródła, Monitoring bezpiecznie odczytuje ich zmiany, Dystrybucja przekazuje przyjęte dane z zachowaniem właściwych kontraktów, a moduły funkcjonalne stosują reguły swojej domeny.

## Wspólne zasady

- Stosujemy spójne reguły lokalizacji, profili, fizycznego File ID, deduplikacji i ownership. Nie zastępujemy nieznanej tożsamości fizycznej domysłem opartym na nazwie lub ścieżce.
- Niepewny lub niekompletny wynik Discovery nie jest dowodem usunięcia źródła. Konflikt właścicieli nie może prowadzić do arbitralnego przypisania danych.
- Monitoring prowadzi przyrostowy odczyt z uwzględnieniem ciągłości, checkpointów, rotacji i bezpiecznego cyklu życia. Reakcja na utratę ciągłości wynika z kontraktu projektu, bez nieuzgodnionego replay ani rebaseline.
- Dystrybucja chroni wymaganą kolejność, izolację profili i poprawny handoff. Checkpoint może postępować tylko zgodnie z kontraktem przyjęcia danych; nie zakładamy kolejności między niezależnymi strumieniami bez dowodu.
- Ograniczamy zbędne I/O i utrzymujemy stabilność oraz prostotę. Błędy i niepewność pozostają jawne.
- Przenosimy sprawdzone rozwiązania między projektami zamiast niezależnie implementować ten sam mechanizm, o ile odpowiadają jego kontraktowi. Ten sam problem powinien mieć możliwie tę samą regułę rozwiązania i porównywalne testy.

## Uzasadnione różnice

- **WMC:** zachowuje niezależne FIFO Mining i Context oraz swój kontrakt handoffu i checkpointów.
- **WMN:** uwzględnia Event i Chat, powiadomienia oraz wiarygodność kontekstu serwera.
- **WST:** zachowuje odrębny kontrakt Skills, przenośny Core i izolację zależności systemowych.

## Rozwój

Naprawiamy konkretne, potwierdzone błędy małymi krokami i dodajemy odpowiednie regresje. Różnice między projektami muszą mieć techniczne uzasadnienie. Nie abstrahujemy bez potrzeby, nie tworzymy frameworków, dodatkowych warstw ani wspólnej biblioteki na zapas i nie przebudowujemy działających mechanizmów wyłącznie dla podobieństwa.

Docelowy WurmLogMonitor ma umożliwiać współdzielenie Discovery, Monitoringu i Dystrybucji przez moduły Mining, Messages i Skills, bez ponownego pisania tych fundamentów. Jest to kierunek docelowy, a nie deklaracja istniejącej integracji.
