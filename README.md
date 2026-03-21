# Tactical Situation Display

Tactical Situation Display on Windows-sovellus, joka näyttää oman koneesi ympärillä olevan ilmatilannekuvan selkeänä 2D-näkymänä.

Sovellus on tehty erityisesti VSOA-käyttöön. Se voi näyttää esimerkiksi:
- oman koneen sijainnin ja suunnan
- lähialueen muun liikenteen
- erikseen merkityt ystävät, paketin jäsenet ja tukikoneet
- kohteiden etäisyyden, korkeuseron ja suunnan

## Quick Start

1. Lataa uusin julkaisu GitHub Releases -sivulta.
2. Pura paketti haluamaasi kansioon.
3. Käynnistä `TacticalDisplay.App.exe`.
4. Kokeile ensin `Demo`-tilaa.
5. Jos haluat käyttää simua, vaihda `Source`-asetukseksi `SimConnect` ja paina `Apply Source`.

## Mitä tarvitset

- Windows-tietokoneen
- Microsoft Flight Simulatorin, jos haluat käyttää oikeaa simudataa

Voit käyttää sovellusta myös ilman simuyhteyttä demotilassa.

## Lataus ja käynnistys

1. Lataa uusin julkaistu versio projektin GitHub Releases -sivulta.
2. Pura paketti haluamaasi kansioon.
3. Käynnistä `TacticalDisplay.App.exe`.

Julkaisupaketissa on valmiina kaikki, mitä sovellus tarvitsee. Sitä ei tarvitse rakentaa itse.

## Ensimmäinen käyttö

Kun sovellus käynnistyy ensimmäisen kerran:
- oletustila on `Demo`
- näkymä näyttää testiliikennettä
- asetuspaneeli on näkyvissä oikealla
- sovellus luo tarvittavat asetustiedostot automaattisesti

Demo-tila sopii siihen, että tarkistat miltä näkymä näyttää ja miten ohjaimet toimivat.

## Käyttötilat

Sovelluksessa on kaksi käyttötilaa:

- `Demo` näyttää testidataa
- `SimConnect` käyttää Microsoft Flight Simulatorin dataa

Voit vaihtaa tilaa sovelluksen asetuspaneelista:

1. Valitse `Source`-kohdasta haluamasi tila.
2. Paina `Apply Source`.
3. Halutessasi tallenna valinta `Save Settings` -painikkeella.

## SimConnect on mukana valmiina

SimConnect on bundlattu sovellukseen. Käyttäjän ei tarvitse asentaa tai kopioida `SimConnect.dll`-tiedostoa erikseen.

Jos Microsoft Flight Simulator on käynnissä ja sovellus on asetettu `SimConnect`-tilaan, yhteys toimii normaalisti ilman lisävaiheita.

Jos yhteys ei muodostu, sovellus voi pyytää valitsemaan Microsoft Flight Simulatorin `exe`-tiedoston tai SimConnect-kirjaston. Tämä on tarkoitettu vain ongelmatilanteisiin.

## Näkymän käyttö

Päänäkymä on oman koneen ympärille keskitetty taktinen näyttö.

Voit muuttaa näkymää suoraan sovelluksesta:
- `Range +` ja `Range -` muuttavat näkyvän alueen laajuutta
- `N/HDG` vaihtaa pohjoinen ylhäällä / koneen suunta ylhäällä -näkymän
- `Declutter` vähentää ruudulla näkyvää tietomäärää
- `Labels` vaihtaa labelitilaa
- `Trails` näyttää tai piilottaa kohteiden jälkiviivat
- `Callsigns ON/OFF` vaihtaa näytetäänkö labelissa suora kutsumerkki

## Labelit ja symbolit

Kohteiden symbolit kertovat niiden tyypin:
- ystävä = ympyrä
- package = vinoneliö
- support = neliö
- enemy = rasti
- tuntematon = piste

Labelit toimivat kolmessa tilassa:
- `Full` näyttää enemmän tietoa
- `Minimal` näyttää tärkeimmät tiedot
- `Off` piilottaa labelit

`Callsigns ON/OFF` vaikuttaa siihen, näkyykö labelissa kohteen suora kutsumerkki:
- `Callsigns ON` näyttää SimConnectilta saadun callsignin, jos se on saatavilla
- `Callsigns OFF` piilottaa suoran callsignin realistisemman näkymän vuoksi

## Kohteiden muokkaus hiirellä

Yksittäistä kohdetta voi muokata suoraan näkymässä:

- vasen hiiren painike kohteen päällä vaihtaa kohteen luokitusta: normaali -> friendly -> enemy -> normaali
- oikea hiiren painike kohteen päällä avaa nimen vaihdon

Nämä muutokset tallentuvat sovelluksen asetustiedostoihin.

## Pikanäppäimet

Sovelluksessa on myös muutama pikanäppäin:

- `Ctrl+H` näyttää tai piilottaa asetuspaneelin
- `Ctrl+D` vaihtaa declutter-tilaa
- `Ctrl+T` kiinnittää ikkunan muiden ikkunoiden päälle tai poistaa kiinnityksen

## Mitä alareunan tila kertoo

Sovelluksen alareunassa näkyy ajantasainen tilatieto, kuten:

- onko yhteys muodostettu
- montako kohdetta näkyy
- SimConnectin tila
- onko kutsumerkillistä liikennettä havaittu
- päivitysnopeus
- käytössä oleva datalähde

## Asetusten tallennus

Tavalliset asetukset voi vaihtaa suoraan sovelluksesta. Kun haluat säilyttää muutokset seuraavaa käynnistystä varten, paina `Save Settings`.

Sovellus tallentaa asetuksia myös suljettaessa.

## Asetustiedostot

Sovellus luo ensimmäisellä käynnistyksellä `config`-kansion automaattisesti `exe`-tiedoston viereen.

Tärkeimmät tiedostot ovat:
- `config/display.json` sisältää yleiset näyttö- ja datalähdeasetukset
- `config/friends.json` sisältää ystäviksi merkittävät kutsumerkit
- `config/package.json` sisältää package-ryhmän kutsumerkit
- `config/support.json` sisältää support-ryhmän kutsumerkit
- `config/manual-targets.json` sisältää käsin annetut nimet ja luokitukset

Useimmille käyttäjille sovelluksen oma käyttöliittymä riittää, eikä tiedostoja tarvitse muokata käsin.

## Jos yhteys ei muodostu

Tarkista ensin:
- Microsoft Flight Simulator on käynnissä
- sovellus on `SimConnect`-tilassa
- käytössä on uusin julkaistu versio

Jos yhteys ei silti muodostu:
1. sulje Tactical Situation Display
2. varmista että simulaattori on käynnissä
3. käynnistä Tactical Situation Display uudelleen

## Kenelle tämä on tarkoitettu

Tämä sovellus on tarkoitettu käyttäjälle, joka haluaa nopeasti luettavan taktisen tilanteen oman koneen ympäriltä ilman raskasta asennusta tai erillistä SimConnect-säätöä.
